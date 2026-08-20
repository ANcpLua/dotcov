using System.Globalization;
using System.Xml;
using DotCov.Formatters;

namespace DotCov.Tool;

/// <summary>
/// The dotcov command-line surface, extracted from Program.cs so exit codes and error paths
/// are testable in-process. Program.cs hands it the real console writers; tests hand it
/// StringWriters (which also disables ANSI coloring, since only a real console gets color).
/// </summary>
public static class DotCovCli
{
    /// <summary>Exit code for an unrecognized command — distinct from 1 (a documented failure) so a typo'd `check` in CI can never look like a clean gate.</summary>
    public const int UnknownCommandExitCode = 2;

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var console = ReferenceEquals(stdout, Console.Out);
        if (console) Ansi.EnableOnWindows();
        var color = console && Ansi.IsSupported();

        var (command, options) = ParseArgs(args);

        try
        {
            return command switch
            {
                "report" => await Report(options, stdout, stderr, color),
                "check" => await Check(options, stdout, stderr),
                "diff" => Diff(options, stdout, stderr, color),
                "snapshot" => await Snapshot(options, stdout, stderr),
                "version" => Version(stdout),
                "help" or "--help" or "-h" => Help(stdout),
                _ => UnknownCommand(command, stdout, stderr)
            };
        }
        catch (Exception ex) when (ex is CliError or XmlException or IOException or UnauthorizedAccessException)
        {
            // Expected failure modes — malformed XML, missing/unreadable paths, DTD refusal,
            // char-cap overflow — get a one-line actionable message, never a stack trace.
            stderr.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    static async Task<int> Report(Dictionary<string, string> opts, TextWriter stdout, TextWriter stderr, bool color)
    {
        if (!opts.TryGetValue("file", out var path))
        {
            stderr.WriteLine("Usage: dotcov report <path> [--format table|json|md] [--threshold N] [--exclude-generated]");
            return 1;
        }

        if (!TryGetFormat(opts, stderr, out var format)) return 1;

        double? threshold = null;
        if (opts.TryGetValue("threshold", out var raw))
        {
            if (!TryParsePercent("threshold", raw, stderr, out var parsed)) return 1;
            threshold = parsed;
        }

        var report = ApplyExclusions(ParseInput(path), opts);

        var output = format switch
        {
            "json" => JsonFormatter.Format(report),
            "markdown" or "md" => MarkdownFormatter.Format(report, threshold),
            _ => TableFormatter.Format(report, color)
        };

        stdout.Write(output);

        if (opts.ContainsKey("github-summary"))
            WriteGitHubSummary(MarkdownFormatter.Format(report, threshold), stderr);

        return await MaybeUpload(opts, () => JsonFormatter.Format(report), stderr);
    }

    static async Task<int> Check(Dictionary<string, string> opts, TextWriter stdout, TextWriter stderr)
    {
        if (!opts.TryGetValue("file", out var path))
        {
            stderr.WriteLine("Usage: dotcov check <path> --min-line N [--min-branch N] [--exclude-generated]");
            return 1;
        }

        if (!TryParsePercent("min-line", opts.GetValueOrDefault("min-line", "80"), stderr, out var minLine))
            return 1;

        if (!TryParsePercent("min-branch", opts.GetValueOrDefault("min-branch", "0"), stderr, out var minBranch))
            return 1;

        var report = ApplyExclusions(ParseInput(path), opts);
        var gate = report.Evaluate(minLine, minBranch);

        // Written on pass AND fail, and derived from the same GateResult as the exit code.
        // Previously the summary was fail-only and re-evaluated with min-branch 0, so a
        // branch-gate failure exited 1 while the PR summary showed a green ✅ badge.
        if (opts.ContainsKey("github-summary"))
            WriteGitHubSummary(GateSummary(report, gate), stderr);

        if (gate.IsPass)
        {
            stdout.WriteLine(gate.ToString());
            return await MaybeUpload(opts, () => JsonFormatter.Format(report), stderr);
        }

        stderr.WriteLine(gate.ToString());

        foreach (var f in report.BelowPercent(minLine))
            stderr.WriteLine(FormattableString.Invariant($"  {f.Path}: {f.LineRate!.Value * 100:F1}%"));

        // Failing runs upload too — red runs are the ones a coverage dashboard most needs.
        // The gate's exit 1 wins regardless of the upload outcome.
        await MaybeUpload(opts, () => JsonFormatter.Format(report), stderr);

        // POLICY (shell, not core - open question for the effectful pass): NoData and Disabled both
        // exit 1 here, deliberately conservative. Whether they deserve a distinct exit code (2?) is a
        // CLI contract change that ripples into every consumer's CI, so it is not decided in this
        // change. GateResult.Outcome carries the distinction whenever that call gets made.
        return 1;
    }

    static int Diff(Dictionary<string, string> opts, TextWriter stdout, TextWriter stderr, bool color)
    {
        if (!opts.TryGetValue("before", out var before) || !opts.TryGetValue("after", out var after))
        {
            stderr.WriteLine("Usage: dotcov diff <before> <after> [--format table|json|md]");
            return 1;
        }

        if (!TryGetFormat(opts, stderr, out var format)) return 1;

        var result = CoverageDiff.Compare(ParseInput(before), ParseInput(after));

        stdout.Write(format switch
        {
            "json" => JsonFormatter.FormatDiff(result),
            "markdown" or "md" => MarkdownFormatter.FormatDiff(result),
            _ => TableFormatter.FormatDiff(result, color)
        });

        return 0;
    }

    static async Task<int> Snapshot(Dictionary<string, string> opts, TextWriter stdout, TextWriter stderr)
    {
        if (!opts.TryGetValue("file", out var path))
        {
            stderr.WriteLine("Usage: dotcov snapshot <path> --commit <sha> --branch <branch> --project <name>");
            return 1;
        }

        var report = ApplyExclusions(ParseInput(path), opts);
        var fileHash = File.Exists(path) ? FileHasher.ComputeHash(path) : null;

        var snapshot = new CoverageSnapshot(
            CommitSha: opts.GetValueOrDefault("commit", "unknown"),
            Branch: opts.GetValueOrDefault("branch", "unknown"),
            Project: opts.GetValueOrDefault("project", "unknown"),
            Timestamp: TimeProvider.System.GetUtcNow(),
            FileHash: fileHash,
            Report: report);

        var json = JsonFormatter.FormatSnapshot(snapshot);
        stdout.Write(json);

        return await MaybeUpload(opts, () => json, stderr);
    }

    static int Version(TextWriter stdout)
    {
        stdout.WriteLine($"dotcov {typeof(CoberturaParser).Assembly.GetName().Version}");
        return 0;
    }

    static int UnknownCommand(string command, TextWriter stdout, TextWriter stderr)
    {
        stderr.WriteLine($"Unknown command '{command}'.");
        Help(stdout);
        return UnknownCommandExitCode;
    }

    static int Help(TextWriter stdout)
    {
        stdout.WriteLine("""
            dotcov - Cobertura coverage toolkit

            Commands:
              report   <path> [--format table|json|md] [--threshold N]    Parse and display coverage
              check    <path> --min-line N [--min-branch N]               CI gate (exit 1 if below)
              diff     <before> <after> [--format table|json|md]          Compare two reports
              snapshot <path> --commit SHA --branch B --project P         Create pipeline-ready JSON
              version                                                     Show version

            Global flags:
              --exclude-generated       Skip generated files, migrations, state machines, Program.cs
              --keep <patterns>         Exempt comma-separated paths from --exclude-generated
                                        (e.g. --keep Program.cs to measure a CLI tool's entry point)
              --upload <url>            POST JSON payload to any endpoint
              --github-summary          Write markdown to $GITHUB_STEP_SUMMARY

            <path> can be a file or directory. Directories are scanned for **/coverage.cobertura.xml.

            Examples:
              dotcov report TestResults/
              dotcov report coverage.cobertura.xml --format json --exclude-generated > coverage.json
              dotcov check TestResults/ --min-line 80 --exclude-generated --github-summary
              dotcov report TestResults/ --exclude-generated --keep Program.cs   # measure host bootstrap
              dotcov snapshot TestResults/ --commit abc123 --branch main --project MyApp --upload https://qyl/api/v1/coverage
              dotcov diff before.cobertura.xml after.cobertura.xml --format md
            """);
        return 0;
    }

    // ── Shared helpers ──

    /// <summary>An expected CLI failure whose message is already user-ready (path included).</summary>
    sealed class CliError(string message, Exception? inner = null) : Exception(message, inner);

    // CLI-layer parse shell: same file/directory semantics as CoberturaParser.ParsePath, but
    // errors carry the offending file's path — including which report in a directory aggregate
    // was malformed — without touching the library's exception contract.
    static CoverageReport ParseInput(string path)
    {
        if (File.Exists(path))
            return ParseReportFile(path);

        if (Directory.Exists(path))
        {
            var files = Directory.GetFiles(path, "coverage.cobertura.xml",
                new EnumerationOptions { RecurseSubdirectories = true });

            if (files.Length is 0)
                return CoverageReport.Empty;

            return files
                .OrderBy(static f => f, StringComparer.Ordinal)
                .Select(ParseReportFile)
                .Aggregate(CoverageReport.Merge);
        }

        throw new CliError($"No file or directory at '{path}'.");
    }

    static CoverageReport ParseReportFile(string path)
    {
        try
        {
            return CoberturaParser.ParseFile(path);
        }
        catch (XmlException ex)
        {
            // XmlException knows line/column but not which file — add the path.
            throw new CliError($"{path}: {ex.Message}", ex);
        }
    }

    static bool TryGetFormat(Dictionary<string, string> opts, TextWriter stderr, out string format)
    {
        format = opts.GetValueOrDefault("format", "table");
        if (format is "table" or "json" or "markdown" or "md") return true;

        stderr.WriteLine($"Invalid --format value: '{format}' (expected table, json, markdown, or md).");
        return false;
    }

    // NaN is rejected alongside unparseable input: every comparison against NaN is false, so a
    // NaN threshold renders as "NaN%" while gating nothing.
    static bool TryParsePercent(string flag, string raw, TextWriter stderr, out double value)
    {
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !double.IsNaN(value))
            return true;

        stderr.WriteLine($"Invalid --{flag} value: '{raw}' (expected a number).");
        return false;
    }

    static CoverageReport ApplyExclusions(CoverageReport report, Dictionary<string, string> opts)
    {
        if (!opts.ContainsKey("exclude-generated")) return report;

        var keep = opts.TryGetValue("keep", out var raw)
            ? raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

        return report.Exclude(ExclusionRules.WellKnown, keep);
    }

    // The check summary's badge must derive from the SAME verdict as the exit code.
    // MarkdownFormatter's single-threshold overload re-evaluates with min-branch 0, so the
    // badge and verdict line are injected here from the precomputed GateResult instead.
    static string GateSummary(CoverageReport report, GateResult gate)
    {
        var badge = gate.Outcome switch
        {
            GateOutcome.Pass => " ✅",
            GateOutcome.Fail => " ❌",
            _ => " ⚠️",
        };

        var body = MarkdownFormatter.Format(report);
        const string header = "## Coverage Report";
        if (body.StartsWith(header, StringComparison.Ordinal))
            body = body[header.Length..].TrimStart('\r', '\n');

        var nl = Environment.NewLine;
        return $"{header}{badge}{nl}{nl}`{gate}`{nl}{nl}{body}";
    }

    static void WriteGitHubSummary(string markdown, TextWriter stderr)
    {
        var summaryPath = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (summaryPath is null) return;

        try
        {
            File.AppendAllText(summaryPath, markdown);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The summary is decoration; a bad GITHUB_STEP_SUMMARY path must not change the
            // command's verdict or exit code.
            stderr.WriteLine($"warning: could not write GITHUB_STEP_SUMMARY: {ex.Message}");
        }
    }

    // JSON is built lazily: a table/markdown run with no --upload never pays to serialize it
    // (and never touches the JSON path at all). The Func defers JsonFormatter.Format until we
    // know an upload URL is actually present.
    static async Task<int> MaybeUpload(Dictionary<string, string> opts, Func<string> json, TextWriter stderr)
    {
        if (!opts.TryGetValue("upload", out var url)) return 0;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var response = await http.PostAsync(url,
                new StringContent(json(), System.Text.Encoding.UTF8, "application/json"));

            if (response.IsSuccessStatusCode)
            {
                stderr.WriteLine($"Uploaded to {url} ({response.StatusCode})");
                return 0;
            }

            stderr.WriteLine($"Upload failed: {url} ({response.StatusCode})");
            return 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
        {
            // InvalidOperationException/UriFormatException: malformed or relative URL.
            // TaskCanceledException: the 30-second timeout above.
            stderr.WriteLine($"Upload failed: {url} ({ex.Message})");
            return 1;
        }
    }

    public static (string command, Dictionary<string, string> options) ParseArgs(string[] raw)
    {
        if (raw.Length is 0) return ("help", []);

        var command = raw[0];
        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? pendingKey = null;
        var positional = 0;

        for (var i = 1; i < raw.Length; i++)
        {
            if (raw[i].StartsWith("--"))
            {
                if (pendingKey is not null) parsed[pendingKey] = "true";
                pendingKey = raw[i][2..];
            }
            else if (pendingKey is not null)
            {
                parsed[pendingKey] = raw[i];
                pendingKey = null;
            }
            else
            {
                var key = positional switch
                {
                    0 => command is "diff" ? "before" : "file",
                    1 => "after",
                    _ => $"arg{positional}"
                };
                parsed[key] = raw[i];
                positional++;
            }
        }

        if (pendingKey is not null) parsed[pendingKey] = "true";

        return (command, parsed);
    }
}
