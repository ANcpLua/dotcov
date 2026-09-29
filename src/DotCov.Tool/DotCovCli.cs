using System.Globalization;
using System.Security.Cryptography;
using System.Xml;
using DotCov.Formatters;

namespace DotCov.Tool;

/// <summary>
/// The dotcov command-line surface, extracted from Program.cs so exit codes and error paths
/// are testable in-process. Program.cs hands it the real console writers; tests hand it
/// StringWriters (which also disables ANSI coloring, since only a real console gets color).
/// </summary>
public static partial class DotCovCli
{
    /// <summary>Exit code for an unrecognized command — distinct from 1 (a documented failure) so a typo'd `check` in CI can never look like a clean gate.</summary>
    public const int UnknownCommandExitCode = 2;

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var console = ReferenceEquals(stdout, Console.Out);
        if (console) Ansi.EnableOnWindows();
        var color = console && Ansi.IsSupported();

        // A bare `--` after the command ends dotcov's own arguments. Only test takes what follows
        // (it goes to dotnet test unchanged); every other command rejects the separator.
        var separator = Array.IndexOf(args, "--");
        var forwarded = separator > 0 ? args[(separator + 1)..] : null;
        var (command, options) = ParseArgs(separator > 0 ? args[..separator] : args);

        try
        {
            RejectUnusedArguments(command, options, forwarded);

            return command switch
            {
                "report" => await Report(options, stdout, stderr, color),
                "check" => await Check(options, stdout, stderr),
                "crap" => Crap(options, stdout, stderr, color),
                "diff" => Diff(options, stdout, stderr, color),
                "snapshot" => await Snapshot(options, stdout, stderr),
                "test" => await Test(options, forwarded ?? [], stdout, stderr, color),
                "version" => Version(stdout),
                "help" or "--help" or "-h" => Help(stdout),
                _ => UnknownCommand(command, stdout, stderr)
            };
        }
        catch (ReportParseException ex)
        {
            // Malformed XML, DTD refusal, char-cap overflow: the parser reports which input
            // and where; the message is rendered here, once, at the output boundary.
            await stderr.WriteLineAsync($"error: {ex.SourceName}: {ex.Message}");
            return 1;
        }
        catch (Exception ex) when (ex is CliError or XmlException or IOException or UnauthorizedAccessException)
        {
            // Expected failure modes — missing/unreadable paths, malformed metrics XML — get a
            // one-line actionable message, never a stack trace.
            await stderr.WriteLineAsync($"error: {ex.Message}");
            return 1;
        }
    }

    static async Task<int> Report(Dictionary<string, string> opts, TextWriter stdout, TextWriter stderr, bool color)
    {
        if (!opts.TryGetValue("file", out var path))
        {
            await stderr.WriteLineAsync("error: missing path. Usage: dotcov report <path> [--format table|json|md] [--threshold N] [--exclude-generated]");
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

        var (merged, inputs) = ParseInput(path, pattern, maxChars);
        var report = ApplyExclusions(merged, opts);

        var output = format switch
        {
            "json" => JsonFormatter.Format(report),
            "markdown" or "md" => MarkdownFormatter.Format(report, threshold),
            _ => TableFormatter.Format(report, color)
        };

        await stdout.WriteAsync(output);

        if (opts.ContainsKey("github-summary"))
            WriteGitHubSummary(MarkdownFormatter.Format(report, threshold), stderr);

        // A failed upload is the outcome, so its "error:" line leads the diagnostics.
        var exitCode = await MaybeUpload(opts, () => JsonFormatter.Format(report), stderr);
        WriteInputs(path, inputs, stderr);
        return exitCode;
    }

    static async Task<int> Check(Dictionary<string, string> opts, TextWriter stdout, TextWriter stderr)
    {
        if (!opts.TryGetValue("file", out var path))
        {
            await stderr.WriteLineAsync("error: missing path. Usage: dotcov check <path> --min-line N [--min-branch N] [--exclude-generated]");
            return 1;
        }

        if (!TryParsePercent("min-line", opts.GetValueOrDefault("min-line", "80"), stderr, out var minLine))
            return 1;

        if (!TryParsePercent("min-branch", opts.GetValueOrDefault("min-branch", "0"), stderr, out var minBranch))
            return 1;

        if (!TryGetParseOptions(opts, stderr, out var pattern, out var maxChars)) return 1;

        var (merged, inputs) = ParseInput(path, pattern, maxChars);
        return await Gate(ApplyExclusions(merged, opts), minLine, minBranch, path, inputs, opts, stdout, stderr);
    }

    /// <summary>
    /// The line/branch gate over a parsed report, shared by check and test: step summary, verdict,
    /// offending files, diagnostics, and upload all come from one evaluation.
    /// </summary>
    static async Task<int> Gate(
        CoverageReport report, double minLine, double minBranch, string path, IReadOnlyList<ReportInput> inputs,
        Dictionary<string, string> opts, TextWriter stdout, TextWriter stderr)
    {
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
            await stdout.WriteLineAsync(gate.ToString());
            // A failed upload turns this pass into exit 1; its "error:" line leads stderr.
            var exitCode = await MaybeUpload(opts, () => JsonFormatter.Format(report), stderr);
            WriteDiagnostics(path, inputs, report.Warnings, stderr);
            return exitCode;
        }

        await stderr.WriteLineAsync(gate.ToString());

        // The offender list answers "which files caused the line-gate failure", so it prints
        // only when the LINE gate actually failed. A branch-only failure listing line-threshold
        // files (possibly with 100% branch coverage) blamed the wrong files; NoData/Disabled
        // have no offenders at all.
        if (gate.LineBelowThreshold)
        {
            await stderr.WriteLineAsync("files below line threshold:");
            foreach (var f in report.BelowPercent(minLine))
                await stderr.WriteLineAsync(FormattableString.Invariant($"  {f.Path}: {FloorFailingPercent(f.LineRate!.Value):F1}%"));
        }

        WriteDiagnostics(path, inputs, report.Warnings, stderr);

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
            stderr.WriteLine("error: missing path. Usage: dotcov crap <coverage-path> [--metrics <file>] [--max-crap N] [--top N] [--format table|json|md]");
            return 1;
        }

        if (!TryGetFormat(opts, stderr, out var format)) return 1;

        // Default 30 — the original CRAP threshold (crap4j). A fully covered method scores its
        // own complexity, so a lower default fails well-tested code on complexity alone;
        // --max-crap 6 opts into that stricter gate.
        if (!TryParsePercent("max-crap", opts.GetValueOrDefault("max-crap", "30"), stderr, out var maxCrap))
            return 1;

        if (!TryGetTop(opts, stderr, out var top)) return 1;
        if (!TryGetParseOptions(opts, stderr, out var pattern, out var maxChars)) return 1;

        var inputs = ResolveInputs(path, pattern);
        var parsed = CoberturaParser.ParseMethods(inputs, maxChars);
        var methods = ApplyMethodExclusions(parsed.Methods, opts);
        var report = CrapAnalysis.Analyze(methods, LoadMetrics(opts, maxChars));
        var gate = report.Evaluate(maxCrap);

        stdout.Write(RenderCrap(format, report, gate, top, color));

        // Written on pass AND fail, from the same gate as the exit code — the same
        // no-false-green contract as check's summary.
        if (opts.ContainsKey("github-summary"))
            WriteGitHubSummary(CrapFormatter.FormatMarkdown(report, gate, top), stderr);

        if (gate.IsPass)
        {
            stdout.WriteLine(gate.ToString());
            WriteDiagnostics(path, inputs, parsed.Warnings, stderr);
            return 0;
        }

        // Same fail-closed policy as check: NoData (no scorable methods) exits 1 — a gate that
        // cannot see must not exit 0. stderr first token (FAIL:/NODATA:) discriminates.
        stderr.WriteLine(gate.ToString());
        WriteDiagnostics(path, inputs, parsed.Warnings, stderr);
        return 1;
    }

    /// <summary>The one crap-format dispatch, shared by stdout and any future sink.</summary>
    static string RenderCrap(string format, CrapReport report, CrapGateResult gate, int? top, bool color) =>
        format switch
        {
            "json" => CrapFormatter.FormatJson(report, gate, top),
            "markdown" or "md" => CrapFormatter.FormatMarkdown(report, gate, top),
            _ => CrapFormatter.Format(report, gate, top, color)
        };

    /// <summary>--top: a positive display-truncation count, or null when absent.</summary>
    static bool TryGetTop(Dictionary<string, string> opts, TextWriter stderr, out int? top)
    {
        top = null;
        if (!opts.TryGetValue("top", out var raw)) return true;

        // NumberStyles.None: digits only, so negatives/signs are rejected here.
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var t) || t is 0)
        {
            stderr.WriteLine($"error: Invalid --top value: '{raw}' (expected a positive integer).");
            return false;
        }

        top = t;
        return true;
    }

    /// <summary>--metrics: the parsed complexity table, or null when the flag is absent.</summary>
    static IReadOnlyList<CodeMetricsMember>? LoadMetrics(Dictionary<string, string> opts, long maxChars)
    {
        if (!opts.TryGetValue("metrics", out var metricsPath)) return null;

        if (!File.Exists(metricsPath))
            throw new CliError($"No metrics file at '{metricsPath}'.");
        return CodeMetricsReader.ParseFile(metricsPath, maxChars);
    }

    static int Diff(Dictionary<string, string> opts, TextWriter stdout, TextWriter stderr, bool color)
    {
        if (!opts.TryGetValue("before", out var before) || !opts.TryGetValue("after", out var after))
        {
            stderr.WriteLine("error: missing path. Usage: dotcov diff <before> <after> [--format table|json|md]");
            return 1;
        }

        if (!TryGetFormat(opts, stderr, out var format)) return 1;

        if (!TryGetParseOptions(opts, stderr, out var pattern, out var maxChars)) return 1;

        var (beforeReport, beforeInputs) = ParseInput(before, pattern, maxChars);
        var (afterReport, afterInputs) = ParseInput(after, pattern, maxChars);
        var result = CoverageDiff.Compare(beforeReport, afterReport);

        stdout.Write(format switch
        {
            "json" => JsonFormatter.FormatDiff(result),
            "markdown" or "md" => MarkdownFormatter.FormatDiff(result),
            _ => TableFormatter.FormatDiff(result, color)
        });

        WriteInputs(before, beforeInputs, stderr);
        WriteInputs(after, afterInputs, stderr);
        return 0;
    }

    static async Task<int> Snapshot(Dictionary<string, string> opts, TextWriter stdout, TextWriter stderr)
    {
        if (!opts.TryGetValue("file", out var path))
        {
            await stderr.WriteLineAsync("error: missing path. Usage: dotcov snapshot <path> [--commit <sha>] [--branch <branch>] [--project <name>]");
            return 1;
        }

        if (!TryGetParseOptions(opts, stderr, out var pattern, out var maxChars)) return 1;

        var (merged, inputs) = ParseInput(path, pattern, maxChars);
        var report = ApplyExclusions(merged, opts);
        var fileHash = HashInputs(inputs);

        var snapshot = new CoverageSnapshot(
            CommitSha: opts.GetValueOrDefault("commit", "unknown"),
            Branch: opts.GetValueOrDefault("branch", "unknown"),
            Project: opts.GetValueOrDefault("project", "unknown"),
            Timestamp: TimeProvider.System.GetUtcNow(),
            FileHash: fileHash,
            Report: report);

        var json = JsonFormatter.FormatSnapshot(snapshot);
        await stdout.WriteAsync(json);

        // A failed upload is the outcome, so its "error:" line leads the diagnostics.
        var exitCode = await MaybeUpload(opts, () => json, stderr);

        // Identity flags default to 'unknown' so local experimentation stays frictionless, but
        // the degradation must not be silent — 'unknown' snapshots land in exactly the --upload
        // dashboard path where commit/branch/project identity matters most. Warned only once a
        // snapshot was produced and after the upload outcome, so parse and upload failures keep
        // "error:" as the first stderr token.
        var missing = new List<string>(3);
        if (!opts.ContainsKey("commit")) missing.Add("--commit");
        if (!opts.ContainsKey("branch")) missing.Add("--branch");
        if (!opts.ContainsKey("project")) missing.Add("--project");
        if (missing.Count > 0)
            await stderr.WriteLineAsync($"warning: {string.Join(", ", missing)} not provided; snapshot stamped 'unknown'");

        WriteInputs(path, inputs, stderr);
        return exitCode;
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
        stdout.WriteLine($$"""
            dotcov - Cobertura coverage toolkit

            Commands:
              report   <path> [--format table|json|md] [--threshold N]    Parse and display coverage
              check    <path> --min-line N [--min-branch N]               CI gate (exit 1 if below)
              crap     <path> [--metrics <file>] [--max-crap N] [--top N] CRAP gate: comp^2*(1-cov)^3+comp
                       [--format table|json|md]                           per method (exit 1 if any method
                                                                          is strictly above --max-crap;
                                                                          at-threshold passes; default 30)
              diff     <before> <after> [--format table|json|md]          Compare two reports
              snapshot <path> [--commit SHA] [--branch B] [--project P]   Create pipeline-ready JSON
                                                                          (identity defaults to 'unknown')
              test     [<project>] [--min-line N] [--min-branch N]        Run dotnet test with coverage into
                       [-- <dotnet test arguments>]                       a fresh TestResults/<run>, then
                                                                          report and gate it like check
              version                                                     Show version

            Shared flags (a flag the command does not use is an error, never ignored):
              --exclude-generated       Skip generated files, migrations, GlobalUsings.cs, Program.cs
                                        (report, check, crap, snapshot, test)
              --keep <substrings>       Exempt comma-separated paths from --exclude-generated by
                                        case-insensitive substring match, not globs
                                        (e.g. --keep Program.cs to measure a CLI tool's entry point)
              --pattern <glob>          Report filename to scan directories for: 'filename'
                                        (top level only) or '**/filename' (recursive)
                                        (default {{ReportPattern.DefaultText}})
              --max-chars <N>           Per-file XML character cap (default 50000000; 0 = no cap)
              --upload <url>            POST JSON payload to any endpoint (report, check, snapshot)
              --github-summary          Write markdown to $GITHUB_STEP_SUMMARY (report, check, crap, test)

            <path> can be a file or directory. Directories are scanned for {{ReportPattern.DefaultText}};
            override the filename with --pattern (gcovr and coverage.py emit coverage.xml).
            Every match is merged; when there is more than one, stderr lists them after the result.
            Give each test run a fresh results directory so an earlier run's report is not merged.

            test passes the coverage options of the runner dotnet test uses in this directory
            (DOTNET_TEST_RUNNER, else global.json test.runner, else VSTest). MTP test projects need
            Microsoft.Testing.Extensions.CodeCoverage; VSTest projects get the Code Coverage collector
            from Microsoft.NET.Test.Sdk. dotnet test output goes to stdout; a failed run exits 1
            without evaluating coverage.

            Exit codes:
              0  success; for check, the gate passed
              1  gate failed or was inconclusive (NODATA/DISABLED), or the command could not
                 run: missing path, parse/IO/size-cap error, invalid flag value, unknown flag,
                 extra path, upload failure, failed test run. The stderr first token
                 (FAIL:/NODATA:/DISABLED:/error:) distinguishes these.
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
              dotcov test tests/MyApp.Tests --min-line 80 --exclude-generated -- -c Release
            """);
        return 0;
    }

    // ── Shared helpers ──

    /// <summary>An expected CLI failure whose message is already user-ready (path included).</summary>
    sealed class CliError(string message, Exception? inner = null) : Exception(message, inner);

    // Mirror of the library defaults — these are the values the help text and READMEs document.
    const long DefaultMaxChars = 50_000_000;

    /// <summary>
    /// The one path-to-inputs step, shared by every command. A missing path is a CLI error;
    /// an existing directory without matches yields no inputs, and the gate decides what an
    /// empty report means.
    /// </summary>
    static IReadOnlyList<ReportInput> ResolveInputs(string path, ReportPattern pattern)
    {
        try
        {
            return ReportResolver.Resolve(path, pattern);
        }
        catch (FileNotFoundException ex)
        {
            throw new CliError(ex.Message, ex);
        }
    }

    /// <summary>Parse and merge what <paramref name="path"/> resolves to; the inputs come back so they can be named.</summary>
    static (CoverageReport Report, IReadOnlyList<ReportInput> Inputs) ParseInput(string path, ReportPattern pattern, long maxChars)
    {
        var inputs = ResolveInputs(path, pattern);
        return (CoberturaParser.Parse(inputs, maxChars), inputs);
    }

    /// <summary>
    /// Every report a path resolved to, when there was more than one. All matches are merged, so
    /// a report an earlier run left in the same directory silently lifts the result; naming them
    /// makes that visible. Written after the verdict, which stays the first stderr token.
    /// </summary>
    static void WriteInputs(string path, IReadOnlyList<ReportInput> inputs, TextWriter stderr)
    {
        if (inputs.Count < 2) return;

        stderr.WriteLine(FormattableString.Invariant($"merged {inputs.Count} reports from '{path}':"));
        foreach (var input in inputs)
            stderr.WriteLine($"  {input.SourceName}");
    }

    /// <summary>A gate's diagnostics, after its verdict: every warning, then the merged reports.</summary>
    static void WriteDiagnostics(
        string path, IReadOnlyList<ReportInput> inputs, IReadOnlyList<CoverageWarning> warnings, TextWriter stderr)
    {
        WriteWarnings(warnings, stderr);
        WriteInputs(path, inputs, stderr);
    }

    /// <summary>
    /// SHA-256 over the resolved reports in resolution order: for a single file exactly
    /// <see cref="FileHasher.ComputeHash"/>, for a directory the set that was merged. Null when
    /// nothing was resolved.
    /// </summary>
    static string? HashInputs(IReadOnlyList<ReportInput> inputs)
    {
        if (inputs.Count is 0) return null;

        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        foreach (var input in inputs)
        {
            using var stream = input.OpenStream();
            int read;
            while ((read = stream.Read(buffer)) > 0)
                sha256.AppendData(buffer.AsSpan(0, read));
        }

        return Convert.ToHexStringLower(sha256.GetHashAndReset());
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
        Dictionary<string, string> opts, TextWriter stderr, out ReportPattern pattern, out long maxChars)
    {
        pattern = ReportPattern.Default;
        maxChars = DefaultMaxChars;

        // A CliError rather than a stderr line: the pattern only matters once a directory is
        // scanned, and the "error:" prefix is the documented discriminator for could-not-run.
        if (opts.TryGetValue("pattern", out var rawPattern) && !ReportPattern.TryParse(rawPattern, out pattern!))
            throw new CliError($"Unsupported pattern '{rawPattern}': only 'filename' and '**/filename' are supported.");

        if (opts.TryGetValue("max-chars", out var raw) &&
            // NumberStyles.None: digits only — a sign or separator makes the value invalid,
            // so negatives are rejected here rather than crashing XmlReaderSettings.
            !long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out maxChars))
        {
            stderr.WriteLine($"error: Invalid --max-chars value: '{raw}' (expected a non-negative integer; 0 = no cap).");
            return false;
        }

        return true;
    }

    static bool TryGetFormat(Dictionary<string, string> opts, TextWriter stderr, out string format)
    {
        format = opts.GetValueOrDefault("format", "table");
        if (format is "table" or "json" or "markdown" or "md") return true;

        stderr.WriteLine($"error: Invalid --format value: '{format}' (expected table, json, markdown, or md).");
        return false;
    }

    // NaN is rejected alongside unparseable input: every comparison against NaN is false, so a
    // NaN threshold renders as "NaN%" while gating nothing.
    static bool TryParsePercent(string flag, string raw, TextWriter stderr, out double value)
    {
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !double.IsNaN(value))
            return true;

        stderr.WriteLine($"error: Invalid --{flag} value: '{raw}' (expected a number).");
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

    /// <summary>Every parser diagnostic, one line each, so a degraded input is never silent.</summary>
    static void WriteWarnings(IReadOnlyList<CoverageWarning> warnings, TextWriter stderr)
    {
        foreach (var w in warnings)
            stderr.WriteLine(w.Line > 0
                ? $"warning: {w.File}:{w.Line}: {w.Detail}"
                : w.File.Length > 0 ? $"warning: {w.File}: {w.Detail}" : $"warning: {w.Detail}");
    }

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
                await stderr.WriteLineAsync($"Uploaded to {url} ({response.StatusCode})");
                return 0;
            }

            await stderr.WriteLineAsync($"error: Upload failed: {url} ({response.StatusCode})");
            return 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException or NotSupportedException)
        {
            // InvalidOperationException/UriFormatException: malformed or relative URL.
            // TaskCanceledException: the 30-second timeout above.
            // NotSupportedException: documented HttpClient behavior for non-http(s) schemes
            // (e.g. ftp://) — thrown before any connection is attempted.
            await stderr.WriteLineAsync($"error: Upload failed: {url} ({ex.Message})");
            return 1;
        }
    }

    // The options each command acts on. Anything else — a misspelled flag, '--name=value', a flag
    // only another command reads, a second path from a shell glob — used to be accepted and
    // ignored, so a gate could pass on defaults nobody asked for. help and version take none.
    static readonly Dictionary<string, HashSet<string>> AcceptedOptions = new(StringComparer.Ordinal)
    {
        ["report"] = Accept("file", "format", "threshold", "exclude-generated", "keep", "pattern", "max-chars", "github-summary", "upload"),
        ["check"] = Accept("file", "min-line", "min-branch", "exclude-generated", "keep", "pattern", "max-chars", "github-summary", "upload"),
        ["crap"] = Accept("file", "format", "max-crap", "top", "metrics", "exclude-generated", "keep", "pattern", "max-chars", "github-summary"),
        ["diff"] = Accept("before", "after", "format", "pattern", "max-chars"),
        ["snapshot"] = Accept("file", "commit", "branch", "project", "exclude-generated", "keep", "pattern", "max-chars", "upload"),
        ["test"] = Accept("file", "min-line", "min-branch", "exclude-generated", "keep", "github-summary"),
    };

    static HashSet<string> Accept(params string[] names) => new(names, StringComparer.OrdinalIgnoreCase);

    /// <summary>Fail before any report is read or test is run when an argument would otherwise be ignored.</summary>
    static void RejectUnusedArguments(string command, Dictionary<string, string> options, string[]? forwarded)
    {
        if (!AcceptedOptions.TryGetValue(command, out var accepted)) return;

        if (forwarded is not null && command is not "test")
            throw new CliError($"Unknown option '--' for '{command}'.");

        foreach (var (key, value) in options)
        {
            if (accepted.Contains(key)) continue;

            // ParseArgs files every path beyond the ones a command takes as "arg<N>".
            if (key.StartsWith("arg", StringComparison.Ordinal) &&
                int.TryParse(key.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out _))
                throw new CliError(command is "test"
                    ? $"Unexpected argument '{value}' for 'test'. Pass dotnet test arguments after '--'."
                    : $"Unexpected argument '{value}' for '{command}'. To merge several reports, pass their directory.");

            var equalsSign = key.IndexOf('=');
            throw new CliError(equalsSign > 0
                ? $"Unknown option '--{key}' for '{command}'. Write '--{key[..equalsSign]} {key[(equalsSign + 1)..]}'."
                : command is "test"
                    ? $"Unknown option '--{key}' for 'test'. Pass dotnet test arguments after '--'."
                    : $"Unknown option '--{key}' for '{command}'.");
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
                    1 when command is "diff" => "after",
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
