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
                "crap" => Crap(options, stdout, stderr, color),
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

        if (!TryGetParseOptions(opts, stderr, out var pattern, out var maxChars)) return 1;

        var report = ApplyExclusions(ParseInput(path, pattern, maxChars), opts);

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

        if (!TryGetParseOptions(opts, stderr, out var pattern, out var maxChars)) return 1;

        var report = ApplyExclusions(ParseInput(path, pattern, maxChars), opts);
        var gate = report.Evaluate(minLine, minBranch);

        // Written on pass AND fail, and derived from the same GateResult as the exit code.
        // Previously the summary was fail-only and re-evaluated with min-branch 0, so a
        // branch-gate failure exited 1 while the PR summary showed a green ✅ badge.
        // MarkdownFormatter.Format(report, gate) renders badge, both thresholds, floored
        // failing-dimension rates, AND the one-line verdict from the same precomputed
        // GateResult — no re-evaluation, no CLI-side splicing.
        if (opts.ContainsKey("github-summary"))
            WriteGitHubSummary(MarkdownFormatter.Format(report, gate), stderr);

        if (gate.IsPass)
        {
            stdout.WriteLine(gate.ToString());
            return await MaybeUpload(opts, () => JsonFormatter.Format(report), stderr);
        }

        stderr.WriteLine(gate.ToString());

        // The offender list answers "which files caused the line-gate failure", so it prints
        // only when the LINE gate actually failed. A branch-only failure listing line-threshold
        // files (possibly with 100% branch coverage) blamed the wrong files; NoData/Disabled
        // have no offenders at all.
        if (gate.LineBelowThreshold)
        {
            stderr.WriteLine("files below line threshold:");
            foreach (var f in report.BelowPercent(minLine))
                stderr.WriteLine(FormattableString.Invariant($"  {f.Path}: {FloorFailingPercent(f.LineRate!.Value):F1}%"));
        }

        // Failing runs upload too — red runs are the ones a coverage dashboard most needs.
        // The gate's exit 1 wins regardless of the upload outcome.
        await MaybeUpload(opts, () => JsonFormatter.Format(report), stderr);

        // POLICY (shell, not core): NoData and Disabled both exit 1 here, deliberately
        // conservative — a gate that cannot see must not exit 0. The contract (0 = pass,
        // 1 = fail or inconclusive or could-not-measure, 2 = unknown command) is documented in
        // help and both READMEs; the stderr first token (FAIL:/NODATA:/DISABLED:/error:) is the
        // only machine-readable discriminator today. A distinct inconclusive exit code would be
        // a CLI contract change that ripples into every consumer's CI, so it stays opt-in-later;
        // GateResult.Outcome carries the distinction whenever that call gets made.
        return 1;
    }

    static int Crap(Dictionary<string, string> opts, TextWriter stdout, TextWriter stderr, bool color)
    {
        if (!opts.TryGetValue("file", out var path))
        {
            stderr.WriteLine("Usage: dotcov crap <coverage-path> [--metrics <file>] [--max-crap N] [--top N] [--format table|json|md]");
            return 1;
        }

        if (!TryGetFormat(opts, stderr, out var format)) return 1;

        // Default 6 — Uncle Bob's agent threshold: low enough that an agent looping against the
        // gate keeps every method trivially testable, high enough that a fully covered comp-6
        // method still passes.
        if (!TryParsePercent("max-crap", opts.GetValueOrDefault("max-crap", "6"), stderr, out var maxCrap))
            return 1;

        int? top = null;
        if (opts.TryGetValue("top", out var rawTop))
        {
            // NumberStyles.None: digits only, so negatives/signs are rejected here.
            if (!int.TryParse(rawTop, NumberStyles.None, CultureInfo.InvariantCulture, out var t) || t is 0)
            {
                stderr.WriteLine($"Invalid --top value: '{rawTop}' (expected a positive integer).");
                return 1;
            }
            top = t;
        }

        if (!TryGetParseOptions(opts, stderr, out var pattern, out var maxChars)) return 1;

        var methods = ApplyMethodExclusions(ParseMethodsInput(path, pattern, maxChars), opts);

        IReadOnlyList<CodeMetricsMember>? metrics = null;
        if (opts.TryGetValue("metrics", out var metricsPath))
        {
            if (!File.Exists(metricsPath))
                throw new CliError($"No metrics file at '{metricsPath}'.");
            metrics = CodeMetricsReader.ParseFile(metricsPath, maxChars);
        }

        var report = CrapAnalysis.Analyze(methods, metrics);
        var gate = report.Evaluate(maxCrap);

        stdout.Write(format switch
        {
            "json" => CrapFormatter.FormatJson(report, gate, top),
            "markdown" or "md" => CrapFormatter.FormatMarkdown(report, gate, top),
            _ => CrapFormatter.Format(report, gate, top, color)
        });

        // Written on pass AND fail, from the same gate as the exit code — the same
        // no-false-green contract as check's summary.
        if (opts.ContainsKey("github-summary"))
            WriteGitHubSummary(CrapFormatter.FormatMarkdown(report, gate, top), stderr);

        if (gate.IsPass)
        {
            stdout.WriteLine(gate.ToString());
            return 0;
        }

        // Same fail-closed policy as check: NoData (no scorable methods) exits 1 — a gate that
        // cannot see must not exit 0. stderr first token (FAIL:/NODATA:) discriminates.
        stderr.WriteLine(gate.ToString());
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

        if (!TryGetParseOptions(opts, stderr, out var pattern, out var maxChars)) return 1;

        var result = CoverageDiff.Compare(
            ParseInput(before, pattern, maxChars), ParseInput(after, pattern, maxChars));

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
            stderr.WriteLine("Usage: dotcov snapshot <path> [--commit <sha>] [--branch <branch>] [--project <name>]");
            return 1;
        }

        if (!TryGetParseOptions(opts, stderr, out var pattern, out var maxChars)) return 1;

        var report = ApplyExclusions(ParseInput(path, pattern, maxChars), opts);
        var fileHash = File.Exists(path) ? FileHasher.ComputeHash(path) : null;

        // Identity flags default to 'unknown' so local experimentation stays frictionless, but
        // the degradation must not be silent — 'unknown' snapshots land in exactly the --upload
        // dashboard path where commit/branch/project identity matters most. Warned only once a
        // snapshot is actually being produced, so parse failures keep a clean "error:" stderr.
        var missing = new List<string>(3);
        if (!opts.ContainsKey("commit")) missing.Add("--commit");
        if (!opts.ContainsKey("branch")) missing.Add("--branch");
        if (!opts.ContainsKey("project")) missing.Add("--project");
        if (missing.Count > 0)
            stderr.WriteLine($"warning: {string.Join(", ", missing)} not provided; snapshot stamped 'unknown'");

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
              crap     <path> [--metrics <file>] [--max-crap N] [--top N] CRAP gate: comp^2*(1-cov)^3+comp
                       [--format table|json|md]                           per method (exit 1 if any method
                                                                          is strictly above --max-crap;
                                                                          at-threshold passes; default 6)
              diff     <before> <after> [--format table|json|md]          Compare two reports
              snapshot <path> [--commit SHA] [--branch B] [--project P]   Create pipeline-ready JSON
                                                                          (identity defaults to 'unknown')
              version                                                     Show version

            Global flags:
              --exclude-generated       Skip generated files, migrations, state machines, Program.cs
              --keep <substrings>       Exempt comma-separated paths from --exclude-generated by
                                        case-insensitive substring match, not globs
                                        (e.g. --keep Program.cs to measure a CLI tool's entry point)
              --pattern <glob>          Report filename to scan directories for: 'filename'
                                        (top level only) or '**/filename' (recursive)
                                        (default **/coverage.cobertura.xml)
              --max-chars <N>           Per-file XML character cap (default 50000000; 0 = no cap)
              --upload <url>            POST JSON payload to any endpoint
              --github-summary          Write markdown to $GITHUB_STEP_SUMMARY

            <path> can be a file or directory. Directories are scanned for **/coverage.cobertura.xml;
            override the filename with --pattern (gcovr and coverage.py emit coverage.xml).

            Exit codes:
              0  success; for check, the gate passed
              1  gate failed or was inconclusive (NODATA/DISABLED), or the command could not
                 run: parse/IO/size-cap error, invalid flag value, upload failure. The stderr
                 first token (FAIL:/NODATA:/DISABLED:/error:) distinguishes these.
              2  unknown command

            crap needs cyclomatic complexity per method: coverlet embeds it in the coverage XML
            (used automatically); for other emitters pass --metrics <file> produced by
            `dotnet msbuild /t:Metrics` (Microsoft.CodeAnalysis.Metrics package).

            Examples:
              dotcov report TestResults/
              dotcov report coverage.cobertura.xml --format json --exclude-generated > coverage.json
              dotcov check TestResults/ --min-line 80 --exclude-generated --github-summary
              dotcov crap TestResults/ --max-crap 6 --github-summary
              dotcov crap coverage.cobertura.xml --metrics MyApp.Metrics.xml --top 10
              dotcov report gcovr-output/ --pattern "**/coverage.xml"   # non-Coverlet report names
              dotcov report TestResults/ --exclude-generated --keep Program.cs   # measure host bootstrap
              dotcov snapshot TestResults/ --commit abc123 --branch main --project MyApp --upload https://qyl/api/v1/coverage
              dotcov diff before.cobertura.xml after.cobertura.xml --format md
            """);
        return 0;
    }

    // ── Shared helpers ──

    /// <summary>An expected CLI failure whose message is already user-ready (path included).</summary>
    sealed class CliError(string message, Exception? inner = null) : Exception(message, inner);

    // Mirror of the library defaults (default parameter values are baked into callers at
    // compile time anyway) — these are the values the help text and READMEs document.
    const string DefaultPattern = "**/coverage.cobertura.xml";
    const long DefaultMaxChars = 50_000_000;

    // Thin dispatch onto the library: CoberturaParser.ParseFile already rethrows XmlExceptions
    // with the failing file's path prefixed (so directory aggregates name the malformed report),
    // and FileNotFoundException is an IOException — both land in RunAsync's catch as one-line
    // errors. Only ParseDirectory's unsupported-pattern ArgumentException needs translating:
    // it is not in RunAsync's catch filter and would otherwise crash with a stack trace.
    static CoverageReport ParseInput(string path, string pattern, long maxChars)
    {
        if (File.Exists(path))
            return CoberturaParser.ParseFile(path, maxChars);

        if (Directory.Exists(path))
        {
            try
            {
                return CoberturaParser.ParseDirectory(path, pattern, maxChars);
            }
            catch (ArgumentException ex)
            {
                throw new CliError(ex.Message, ex);
            }
        }

        throw new CliError($"No file or directory at '{path}'.");
    }

    /// <summary>Method-level twin of <see cref="ParseInput"/>, same dispatch and error contract.</summary>
    static IReadOnlyList<MethodCoverage> ParseMethodsInput(string path, string pattern, long maxChars)
    {
        if (File.Exists(path))
            return CoberturaParser.ParseMethodsFile(path, maxChars);

        if (Directory.Exists(path))
        {
            try
            {
                return CoberturaParser.ParseMethodsDirectory(path, pattern, maxChars);
            }
            catch (ArgumentException ex)
            {
                throw new CliError(ex.Message, ex);
            }
        }

        throw new CliError($"No file or directory at '{path}'.");
    }

    /// <summary>Method-level twin of <see cref="ApplyExclusions"/> — same flags, same rule set.</summary>
    static IReadOnlyList<MethodCoverage> ApplyMethodExclusions(
        IReadOnlyList<MethodCoverage> methods, Dictionary<string, string> opts)
    {
        if (!opts.ContainsKey("exclude-generated")) return methods;

        var keep = opts.TryGetValue("keep", out var raw)
            ? raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

        return CrapAnalysis.ExcludeFiles(methods, ExclusionRules.WellKnown, keep);
    }

    /// <summary>Resolve --pattern / --max-chars for the commands that parse coverage input.</summary>
    static bool TryGetParseOptions(
        Dictionary<string, string> opts, TextWriter stderr, out string pattern, out long maxChars)
    {
        pattern = opts.GetValueOrDefault("pattern", DefaultPattern);
        maxChars = DefaultMaxChars;

        if (opts.TryGetValue("max-chars", out var raw) &&
            // NumberStyles.None: digits only — a sign or separator makes the value invalid,
            // so negatives are rejected here rather than crashing XmlReaderSettings.
            !long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out maxChars))
        {
            stderr.WriteLine($"Invalid --max-chars value: '{raw}' (expected a non-negative integer; 0 = no cap).");
            return false;
        }

        return true;
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

    // Display flooring for a rate in a FAILING dimension, one decimal: 79.96% renders 79.9%,
    // never a rounded-up 80.0% that reads as equal to the minimum it missed. GateResult owns
    // this policy (its ToString and the markdown gate overload apply the same floor through
    // the internal GateResult.RateEpsilon = 1e-9, which this epsilon mirrors); this is the
    // single copy on the DotCov.Tool side of the assembly boundary.
    static double FloorFailingPercent(double rate) => Math.Floor(rate * 1000 + 1e-9) / 10;

    static void WriteGitHubSummary(string markdown, TextWriter stderr)
    {
        var summaryPath = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (string.IsNullOrEmpty(summaryPath)) return;

        try
        {
            File.AppendAllText(summaryPath, markdown);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
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
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException or NotSupportedException)
        {
            // InvalidOperationException/UriFormatException: malformed or relative URL.
            // TaskCanceledException: the 30-second timeout above.
            // NotSupportedException: documented HttpClient behavior for non-http(s) schemes
            // (e.g. ftp://) — thrown before any connection is attempted.
            stderr.WriteLine($"Upload failed: {url} ({ex.Message})");
            return 1;
        }
    }

    // Flags that never take a value. Recorded as "true" the moment the token is seen, so a
    // following non-dash token stays positional: `report --exclude-generated cov.xml` must not
    // swallow the path as the flag's value. Comparer matches the parsed dictionary's.
    static readonly HashSet<string> ValuelessFlags =
        new(StringComparer.OrdinalIgnoreCase) { "exclude-generated", "github-summary" };

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
                var key = raw[i][2..];
                if (ValuelessFlags.Contains(key))
                {
                    parsed[key] = "true";
                    pendingKey = null;
                }
                else
                {
                    pendingKey = key;
                }
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
