using System.Globalization;
using System.Security.Cryptography;
using System.Xml;
using DotCov.Formatters;

namespace DotCov.Tool;

/// <summary>
/// The dotcov command-line surface, kept out of Program.cs so exit codes and error paths
/// are testable in-process. Program.cs hands it the real console writers; tests hand it
/// StringWriters (which also disables ANSI coloring, since only a real console gets color).
/// Stdout carries only a command's data; verdicts, warnings, and errors go to stderr, where
/// the verdict of check, crap, and test is the first line. The meaning of each exit code is
/// documented once, in the help's exit-code table.
/// </summary>
public static partial class DotCovCli
{
    /// <summary>Exit code 1: a gate measured coverage below its threshold.</summary>
    public const int FailExitCode = 1;
    /// <summary>Exit code 2: invalid command-line usage.</summary>
    public const int UsageExitCode = 2;
    /// <summary>Exit code 3: a gate lacks the measurements it needs.</summary>
    public const int NoDataExitCode = 3;
    /// <summary>Exit code 4: all thresholds are disabled.</summary>
    public const int DisabledExitCode = 4;
    /// <summary>Exit code 5: an input or IO error.</summary>
    public const int ErrorExitCode = 5;
    /// <summary>Exit code 6: the dotnet test run of `dotcov test` failed.</summary>
    public const int TestRunFailedExitCode = 6;

    public static async Task<int> RunAsync(string[] args, TextWriter stdout, TextWriter stderr)
    {
        var console = ReferenceEquals(stdout, Console.Out);
        if (console) Ansi.EnableOnWindows();

        // A bare `--` after the command ends dotcov's own arguments. Only test takes what follows
        // (it goes to dotnet test unchanged); every other command rejects the separator.
        var separator = Array.IndexOf(args, "--");
        var forwarded = separator > 0 ? args[(separator + 1)..] : null;
        var own = separator > 0 ? args[..separator] : args;

        try
        {
            var command = CommandName(own);

            // --help anywhere among a command's own arguments answers before anything is checked.
            if (own.Skip(1).Any(static a => a is "--help" or "-h"))
            {
                await stdout.WriteAsync(HelpFor(CommandsByName[command]));
                return 0;
            }

            var parsed = Parse(command, own);
            RejectUnusedArguments(command, parsed, forwarded);
            var options = parsed.Options;
            var color = console && !options.ContainsKey("no-color") && Ansi.IsSupported();
            return await Dispatch(command, options, forwarded, stdout, stderr, color);
        }
        catch (ReportParseException ex)
        {
            // Malformed XML, DTD refusal, char-cap overflow: the parser reports which input
            // and where; the message is rendered here, once, at the output boundary.
            await stderr.WriteLineAsync($"error: {ex.SourceName}: {ex.Message}");
            return ErrorExitCode;
        }
        catch (Exception ex)
        {
            // Anything unexpected is a bug and propagates with its stack trace.
            if (!IsExpectedFailure(ex)) throw;

            // Expected failure modes — missing/unreadable paths, malformed metrics XML — get a
            // one-line actionable message, never a stack trace.
            await stderr.WriteLineAsync($"error: {ex.Message}");
            // A --source-root no report declares is a request that cannot be honored, not a
            // report that could not be read.
            return ex is UsageError or UndeclaredSourceRootException ? UsageExitCode : ErrorExitCode;
        }
    }

    static bool IsExpectedFailure(Exception ex) =>
        ex is UsageError or CliError or UndeclaredSourceRootException or XmlException or IOException or
            InvalidDataException or UnauthorizedAccessException;

    /// <summary>The command <paramref name="own"/> names, with its aliases resolved; an unknown one is a usage error.</summary>
    static string CommandName(string[] own)
    {
        if (own.Length is 0) return "help";

        var name = own[0] switch { "--version" => "version", "--help" or "-h" => "help", var other => other };
        return CommandsByName.ContainsKey(name) ? name : throw UnknownCommand(name);
    }

    // Not async: each arm hands back its command's Task, so a synchronous command's exception
    // still surfaces inside RunAsync's try, and the switch stays one branch per command.
    static Task<int> Dispatch(
        string command, Dictionary<string, string> options, string[]? forwarded, TextWriter stdout, TextWriter stderr, bool color) =>
        command switch
        {
            "report" => Report(options, stdout, stderr, color),
            "check" => Check(options, stderr),
            "crap" => Task.FromResult(Crap(options, stdout, stderr, color)),
            "diff" => Task.FromResult(Diff(options, stdout, stderr, color)),
            "snapshot" => Snapshot(options, stdout, stderr),
            "test" => Test(options, forwarded ?? [], stdout, stderr, color),
            "version" => Task.FromResult(Version(stdout)),
            _ => Task.FromResult(Help(options, stdout))
        };

    static async Task<int> Report(Dictionary<string, string> opts, TextWriter stdout, TextWriter stderr, bool color)
    {
        if (!opts.TryGetValue("file", out var path))
            return MissingPath("report", stderr);

        if (!TryGetFormat(opts, stderr, out var format)) return UsageExitCode;

        double? threshold = null;
        if (opts.TryGetValue("threshold", out var raw))
        {
            if (!TryParsePercent("threshold", raw, stderr, out var parsed)) return UsageExitCode;
            threshold = parsed;
        }

        if (!TryGetParseOptions(opts, stderr, out var pattern, out var maxChars)) return UsageExitCode;

        var (merged, inputs) = ParseInput(path, pattern, maxChars, Identity(opts));
        var report = ApplyExclusions(merged, opts);

        var output = format switch
        {
            "json" => JsonFormatter.Format(report),
            "markdown" or "md" => MarkdownFormatter.Format(report, threshold),
            _ => TableFormatter.Format(report, color)
        };

        await stdout.WriteAsync(output);

        // A failed upload is the outcome, so its "error:" line leads the diagnostics.
        var exitCode = await MaybeUpload(opts, () => JsonFormatter.Format(report), stderr);

        if (opts.ContainsKey("github-summary"))
            WriteGitHubSummary(MarkdownFormatter.Format(report, threshold), stderr);

        // The table shows only a warning count; markdown and JSON already list each warning.
        // JSON carries the exclusion itself; the other formats name it on stderr.
        WriteDiagnostics(path, pattern, inputs, format is "table" ? report.Warnings : [],
            format is "json" ? FileExclusion.None : report.Exclusion, stderr);
        return exitCode;
    }

    static async Task<int> Check(Dictionary<string, string> opts, TextWriter stderr)
    {
        if (!opts.TryGetValue("file", out var path))
            return MissingPath("check", stderr);

        if (!TryGetThresholds("check", opts, stderr, out var minLine, out var minBranch)) return UsageExitCode;
        if (!TryGetParseOptions(opts, stderr, out var pattern, out var maxChars)) return UsageExitCode;
        if (!TryGetBadgeDir(opts, stderr)) return UsageExitCode;

        var (merged, inputs) = ParseInput(path, pattern, maxChars, Identity(opts));
        var report = ApplyExclusions(merged, opts);
        return await Gate(report, report.Evaluate(minLine, minBranch), path, pattern, inputs, opts, stderr);
    }

    /// <summary>
    /// The line/branch gate over a parsed report, shared by check and test: verdict, offending
    /// files, badge, upload, step summary, and diagnostics all come from one evaluation, the
    /// <paramref name="gate"/> the caller evaluated (test also renders its table from it). The
    /// verdict is the first stderr line on every outcome, followed by <paramref name="reports"/>
    /// when the caller names where the reports came from. <paramref name="pattern"/> is null when
    /// the reports were not found in a directory the user named, so no --pattern advice applies.
    /// </summary>
    static async Task<int> Gate(
        CoverageReport report, GateResult gate, string path, ReportPattern? pattern, IReadOnlyList<ReportInput> inputs,
        Dictionary<string, string> opts, TextWriter stderr, string? reports = null)
    {
        await stderr.WriteLineAsync(gate.ToString());
        if (reports is not null) await stderr.WriteLineAsync(reports);

        // The offender list answers "which files caused the line-gate failure", so it prints
        // only when the LINE gate actually failed. A branch-only failure listing line-threshold
        // files (possibly with 100% branch coverage) would blame the wrong files; NoData/Disabled
        // have no offenders at all — a branch-NoData gate can still carry a line rate below
        // min-line, so the outcome is checked, not just the line comparison.
        if (gate is { Outcome: GateOutcome.Fail, LineBelowThreshold: true })
        {
            await stderr.WriteLineAsync("files below line threshold:");
            foreach (var f in report.BelowPercent(gate.MinLinePercent))
                await stderr.WriteLineAsync(FormattableString.Invariant($"  {f.Path}: {FloorFailingPercent(f.LineRate!.Value):F1}%"));
        }

        // Every outcome writes its badge and uploads: red runs are the ones a badge and a coverage
        // dashboard most need to show. Either failing turns a pass into exit 5; a non-passing
        // gate keeps its own exit code.
        var badgeWritten = WriteBadge(opts, gate, stderr);
        var uploadExitCode = await MaybeUpload(opts, () => JsonFormatter.Format(report), stderr);

        // Derived from the same GateResult as the exit code: MarkdownFormatter.Format(report, gate)
        // renders badge, both thresholds, floored failing-dimension rates, AND the one-line verdict
        // from it — no re-evaluation, no CLI-side splicing.
        if (opts.ContainsKey("github-summary"))
            WriteGitHubSummary(MarkdownFormatter.Format(report, gate), stderr);
        WriteDiagnostics(path, pattern, inputs, report.Warnings, report.Exclusion, stderr);

        if (!gate.IsPass) return GateExitCode(gate.Outcome);
        return badgeWritten ? uploadExitCode : ErrorExitCode;
    }

    static int GateExitCode(GateOutcome outcome) => outcome switch
    {
        GateOutcome.Pass => 0,
        GateOutcome.NoData => NoDataExitCode,
        GateOutcome.Disabled => DisabledExitCode,
        _ => FailExitCode
    };

    static int Crap(Dictionary<string, string> opts, TextWriter stdout, TextWriter stderr, bool color)
    {
        if (!opts.TryGetValue("file", out var path))
            return MissingPath("crap", stderr);

        if (!TryGetFormat(opts, stderr, out var format)) return UsageExitCode;

        // Default 30 — the original CRAP threshold (crap4j). A fully covered method scores its
        // own complexity, so a lower default fails well-tested code on complexity alone;
        // --max-crap 6 opts into that stricter gate.
        if (!TryParseNumber("max-crap", opts.GetValueOrDefault("max-crap", "30"), stderr, out var maxCrap))
            return UsageExitCode;

        if (!TryGetTop(opts, stderr, out var top)) return UsageExitCode;
        if (!TryGetParseOptions(opts, stderr, out var pattern, out var maxChars)) return UsageExitCode;

        var inputs = ResolveInputs(path, pattern);
        var parsed = CoberturaParser.ParseMethods(inputs, maxChars, Identity(opts));
        var report = CrapAnalysis.Analyze(ApplyExclusions(parsed, opts), LoadMetrics(opts, maxChars));
        var gate = report.Evaluate(maxCrap);
        var exclusion = format is "json" ? FileExclusion.None : report.Exclusion;

        // Stdout holds only the rendering, so `--format json` stays one valid document; the
        // verdict leads stderr in every format, as check's and test's do.
        stdout.Write(RenderCrap(format, report, gate, top, color));
        stderr.WriteLine(gate.ToString());

        // Written on pass AND fail, from the same gate as the exit code — the same
        // no-false-green contract as check's summary.
        if (opts.ContainsKey("github-summary"))
            WriteGitHubSummary(CrapFormatter.FormatMarkdown(report, gate, top), stderr);
        WriteDiagnostics(path, pattern, inputs, parsed.Warnings, exclusion, stderr);
        return GateExitCode(gate.Outcome);
    }

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
            return MissingPath("diff", stderr);

        if (!TryGetFormat(opts, stderr, out var format)) return UsageExitCode;

        if (!TryGetParseOptions(opts, stderr, out var pattern, out var maxChars)) return UsageExitCode;

        var identity = Identity(opts);
        var (beforeReport, beforeInputs) = ParseInput(before, pattern, maxChars, identity);
        var (afterReport, afterInputs) = ParseInput(after, pattern, maxChars, identity);
        var result = CoverageDiff.Compare(beforeReport, afterReport);

        stdout.Write(format switch
        {
            "json" => JsonFormatter.FormatDiff(result),
            "markdown" or "md" => MarkdownFormatter.FormatDiff(result),
            _ => TableFormatter.FormatDiff(result, color)
        });

        WriteDiagnostics(before, pattern, beforeInputs, beforeReport.Warnings, FileExclusion.None, stderr);
        WriteDiagnostics(after, pattern, afterInputs, afterReport.Warnings, FileExclusion.None, stderr);
        return 0;
    }

    static async Task<int> Snapshot(Dictionary<string, string> opts, TextWriter stdout, TextWriter stderr)
    {
        if (!opts.TryGetValue("file", out var path))
            return MissingPath("snapshot", stderr);

        if (!TryGetParseOptions(opts, stderr, out var pattern, out var maxChars)) return UsageExitCode;

        var (merged, inputs) = ParseInput(path, pattern, maxChars, Identity(opts));
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

        WarnNoMatch(path, pattern, inputs, stderr);
        WriteInputs(path, inputs, stderr);
        return exitCode;
    }

    // The package version's three components: the assembly version's fourth is always 0.
    static int Version(TextWriter stdout)
    {
        stdout.WriteLine($"dotcov {typeof(CoberturaParser).Assembly.GetName().Version!.ToString(3)}");
        return 0;
    }

    /// <summary>A command run without its path: its usage line from the help catalog, and where the rest is.</summary>
    static int MissingPath(string command, TextWriter stderr)
    {
        stderr.WriteLine($"error: missing path. Usage: {CommandsByName[command].Usage} (dotcov {command} --help)");
        return UsageExitCode;
    }

    // ── Shared helpers ──

    /// <summary>An expected CLI failure whose message is already user-ready (path included).</summary>
    sealed class CliError(string message, Exception? inner = null) : Exception(message, inner);

    /// <summary>An invalid invocation, identified independently of diagnostic wording.</summary>
    sealed class UsageError(string message) : Exception(message);

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
    static (CoverageReport Report, IReadOnlyList<ReportInput> Inputs) ParseInput(
        string path, ReportPattern pattern, long maxChars, FileIdentity identity)
    {
        var inputs = ResolveInputs(path, pattern);
        return (CoberturaParser.Parse(inputs, maxChars, identity), inputs);
    }

    /// <summary>
    /// --probe-sources / --source-root: how relative filenames resolve under several declared
    /// roots. Lexical by default, so the same report gives the same keys on every machine.
    /// The two flags were checked for conflict and emptiness before anything was read.
    /// </summary>
    static FileIdentity Identity(Dictionary<string, string> opts) =>
        opts.TryGetValue("source-root", out var root) ? FileIdentity.FromSourceRoot(root)
        : opts.ContainsKey("probe-sources") ? FileIdentity.ProbeSources
        : FileIdentity.Lexical;

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

    /// <summary>
    /// A command's diagnostics, after its verdict: a directory that matched nothing, every warning,
    /// the exclusion, then the merged reports. A null <paramref name="pattern"/> skips the
    /// no-match warning, for a directory the user did not name.
    /// </summary>
    static void WriteDiagnostics(
        string path, ReportPattern? pattern, IReadOnlyList<ReportInput> inputs, IReadOnlyList<CoverageWarning> warnings,
        FileExclusion exclusion, TextWriter stderr)
    {
        if (pattern is not null) WarnNoMatch(path, pattern, inputs, stderr);
        WriteWarnings(warnings, stderr);
        WriteExclusion(exclusion, stderr);
        WriteInputs(path, inputs, stderr);
    }

    // Every .xml below the directory, hidden ones included, skipping what cannot be listed: the
    // hint must never turn a NODATA into an IO error.
    static readonly EnumerationOptions XmlSearch = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.None,
        IgnoreInaccessible = true,
        MatchCasing = MatchCasing.CaseInsensitive
    };

    /// <summary>
    /// A directory argument that matched no report is the most invisible misconfiguration there
    /// is: the gate reads it as "nothing was measured". One warning names the pattern and the
    /// directory, and when the directory holds .xml files, up to three of them and the --pattern
    /// that reads the first. A file argument always resolves to itself, so an empty
    /// <paramref name="inputs"/> means a directory.
    /// </summary>
    static void WarnNoMatch(string path, ReportPattern pattern, IReadOnlyList<ReportInput> inputs, TextWriter stderr)
    {
        if (inputs.Count > 0) return;

        var xml = Directory.EnumerateFiles(path, "*.xml", XmlSearch)
            .Select(file => Path.GetRelativePath(path, file))
            .Order(StringComparer.Ordinal)
            .ToList();
        var warning = $"warning: no file in '{path}' matches '{pattern}'";
        if (xml.Count > 0)
        {
            var more = xml.Count > 3 ? FormattableString.Invariant($" (+{xml.Count - 3} more)") : "";
            warning += $"; .xml files there: {string.Join(", ", xml.Take(3))}{more}; " +
                       $"try --pattern \"**/{Path.GetFileName(xml[0])}\"";
        }

        stderr.WriteLine(warning);
    }

    /// <summary>
    /// An active exclusion, named once: its rules, the --keep exemptions, and every file it
    /// removed — a number computed without them must say so.
    /// </summary>
    static void WriteExclusion(FileExclusion exclusion, TextWriter stderr)
    {
        if (!exclusion.IsActive) return;

        stderr.WriteLine($"exclusion rules: {string.Join(", ", exclusion.Patterns)}");
        if (exclusion.Keep.Count > 0)
            stderr.WriteLine($"kept by --keep: {string.Join(", ", exclusion.Keep)}");
        stderr.WriteLine(FormattableString.Invariant($"excluded {exclusion.ExcludedFiles.Count} files:"));
        foreach (var file in exclusion.ExcludedFiles)
            stderr.WriteLine($"  {file}");
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

    /// <summary>Method-level twin of <see cref="ApplyExclusions(CoverageReport, Dictionary{string, string})"/> — same flags, same rule set.</summary>
    static MethodCoverageReport ApplyExclusions(MethodCoverageReport report, Dictionary<string, string> opts) =>
        report.Exclude(ExclusionPatterns(opts), KeepPatterns(opts));

    /// <summary>Resolve --pattern / --max-chars for the commands that parse coverage input.</summary>
    static bool TryGetParseOptions(
        Dictionary<string, string> opts, TextWriter stderr, out ReportPattern pattern, out long maxChars)
    {
        pattern = ReportPattern.Default;
        maxChars = DefaultMaxChars;

        // Invalid option syntax is a usage error, independent of whether the input exists.
        if (opts.TryGetValue("pattern", out var rawPattern) && !ReportPattern.TryParse(rawPattern, out pattern!))
            throw new UsageError($"Unsupported pattern '{rawPattern}': only 'filename' and '**/filename' are supported.");

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

    /// <summary>
    /// --badge-dir, checked before any report is read or test is run: an empty value (an unset
    /// shell variable) names no directory.
    /// </summary>
    static bool TryGetBadgeDir(Dictionary<string, string> opts, TextWriter stderr)
    {
        if (!opts.TryGetValue("badge-dir", out var directory) || directory.Length > 0) return true;

        stderr.WriteLine("error: Invalid --badge-dir value: '' (expected a directory).");
        return false;
    }

    /// <summary>
    /// --badge-dir: the evaluated gate as <see cref="BadgeFormatter.SvgFileName"/> and
    /// <see cref="BadgeFormatter.JsonFileName"/>, written for every outcome, so a failing gate
    /// replaces a green badge. False, after an "error:" line, when the files could not be written.
    /// </summary>
    static bool WriteBadge(Dictionary<string, string> opts, GateResult gate, TextWriter stderr)
    {
        if (!opts.TryGetValue("badge-dir", out var directory)) return true;

        var svg = BadgeFormatter.Svg(gate);
        var json = BadgeFormatter.ShieldsJson(gate);
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, BadgeFormatter.SvgFileName), svg);
            File.WriteAllText(Path.Combine(directory, BadgeFormatter.JsonFileName), json);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"error: could not write the badge to '{directory}': {ex.Message}");
            return false;
        }
    }

    static bool TryGetFormat(Dictionary<string, string> opts, TextWriter stderr, out string format)
    {
        format = opts.GetValueOrDefault("format", "table");
        if (format is "table" or "json" or "markdown" or "md") return true;

        stderr.WriteLine($"error: Invalid --format value: '{format}' (expected table, json, markdown, or md).");
        return false;
    }

    /// <summary>
    /// --min-line / --min-branch for check and test. There is no default: a gate nobody set
    /// a threshold for would pass on a number nobody chose. One flag checks its dimension and
    /// disables the other; both at 0 is the DISABLED gate.
    /// </summary>
    static bool TryGetThresholds(
        string command, Dictionary<string, string> opts, TextWriter stderr, out double minLine, out double minBranch)
    {
        minLine = minBranch = 0;
        var hasLine = opts.TryGetValue("min-line", out var line);
        var hasBranch = opts.TryGetValue("min-branch", out var branch);
        if (!hasLine && !hasBranch)
        {
            stderr.WriteLine($"error: missing threshold: '{command}' needs --min-line N, --min-branch N, or both (0 to 100; 0 disables a dimension).");
            return false;
        }

        return (!hasLine || TryParsePercent("min-line", line!, stderr, out minLine)) &&
               (!hasBranch || TryParsePercent("min-branch", branch!, stderr, out minBranch));
    }

    // A percentage is a finite number from 0 to 100. NaN is rejected alongside unparseable
    // input: every comparison against NaN is false, so a NaN threshold renders as "NaN%" while
    // gating nothing; 150 or infinity could never pass, and a negative value never fails.
    static bool TryParsePercent(string flag, string raw, TextWriter stderr, out double value)
    {
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value is >= 0 and <= 100)
            return true;

        stderr.WriteLine($"error: Invalid --{flag} value: '{raw}' (expected a number from 0 to 100).");
        return false;
    }

    // Any number but NaN: an infinite --max-crap is the documented way to disable the CRAP gate.
    static bool TryParseNumber(string flag, string raw, TextWriter stderr, out double value)
    {
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !double.IsNaN(value))
            return true;

        stderr.WriteLine($"error: Invalid --{flag} value: '{raw}' (expected a number).");
        return false;
    }

    static CoverageReport ApplyExclusions(CoverageReport report, Dictionary<string, string> opts) =>
        report.Exclude(ExclusionPatterns(opts), KeepPatterns(opts));

    /// <summary>The rule sets the exclusion flags select; empty when neither is set, which excludes nothing.</summary>
    static List<string> ExclusionPatterns(Dictionary<string, string> opts)
    {
        List<string> patterns = [];
        if (opts.ContainsKey("exclude-generated")) patterns.AddRange(ExclusionRules.WellKnown);
        if (opts.ContainsKey("exclude-test-code")) patterns.AddRange(ExclusionRules.TestCode);
        return patterns;
    }

    static string[] KeepPatterns(Dictionary<string, string> opts) =>
        opts.TryGetValue("keep", out var raw)
            ? raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];

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
            return ErrorExitCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Transport failure or the 30-second timeout; URL syntax was validated before IO.
            await stderr.WriteLineAsync($"error: Upload failed: {url} ({ex.Message})");
            return ErrorExitCode;
        }
    }

    /// <summary>Fail before any report is read or test is run when an argument would otherwise be ignored.</summary>
    static void RejectUnusedArguments(string command, ParsedArguments parsed, string[]? forwarded)
    {
        if (forwarded is not null && command is not "test")
            throw UnknownOptionError(command, "--", null);

        // What the dictionary cannot show, in order: an option it filed as a path or under a
        // path's slot, then the options it holds, then a flag that got no value — the unknown
        // name is the more useful diagnosis than its missing value.
        if (parsed.Unknown.Count > 0)
            throw parsed.Unknown[0];

        var accepted = AcceptedOptions[command];
        foreach (var (key, value) in parsed.Options)
        {
            if (!accepted.Contains(key)) throw UnknownOption(command, key, value);
            if (value.Length is 0 && (IsPathSlot(command, key) || key.Equals("metrics", StringComparison.OrdinalIgnoreCase)))
                throw new UsageError("missing path: the path argument is empty.");
        }

        if (parsed.WithoutValue.Count > 0)
            throw parsed.WithoutValue[0];

        RejectInvalidValues(parsed.Options);
    }

    /// <summary>The error for an option the command does not take, worded for what it most likely is.</summary>
    static UsageError UnknownOption(string command, string key, string value)
    {
        // Parse files every positional argument beyond the ones a command takes as "arg<N>".
        if (key.StartsWith("arg", StringComparison.Ordinal) &&
            int.TryParse(key.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out _))
            return new UsageError($"Unexpected argument '{value}' for '{command}'." + command switch
            {
                "test" => " Pass dotnet test arguments after '--'.",
                "version" or "help" => "",
                _ => " To merge several reports, pass their directory."
            });

        // An option the command takes, written with '=': the space-separated form works, unless
        // the flag takes no value, when the value would become a stray positional argument.
        var equalsSign = key.IndexOf('=');
        var name = equalsSign > 0 ? key[..equalsSign] : key;
        if (equalsSign > 0 && CommandsByName[command].Options.Contains(name, StringComparer.OrdinalIgnoreCase))
            return UnknownOptionError(command, $"--{key}", null, ValuelessFlags.Contains(name)
                ? $"'--{name}' takes no value."
                : $"Write '--{name} {key[(equalsSign + 1)..]}'.");

        return UnknownOptionError(command, $"--{key}", name, command is "test" ? "Pass dotnet test arguments after '--'." : null);
    }

    /// <summary>Accepted options whose value, or whose combination with another, cannot be honored.</summary>
    static void RejectInvalidValues(Dictionary<string, string> options)
    {
        if (options.TryGetValue("upload", out var url) &&
            (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            throw new UsageError($"Invalid --upload value: '{url}' (expected an absolute http(s) URL).");

        // --keep only exempts files from an exclusion; on its own it would change nothing.
        if (options.ContainsKey("keep") && !options.ContainsKey("exclude-generated") && !options.ContainsKey("exclude-test-code"))
            throw new UsageError("--keep only applies together with --exclude-generated or --exclude-test-code.");

        if (options.ContainsKey("probe-sources") && options.ContainsKey("source-root"))
            throw new UsageError("--probe-sources and --source-root are alternatives: pass one of them.");

        if (options.TryGetValue("source-root", out var root) && string.IsNullOrWhiteSpace(root))
            throw new UsageError($"Invalid --source-root value: '{root}' (expected a declared <source> root).");
    }

    // Flags that never take a value. Recorded as "true" the moment the token is seen, so a
    // following non-dash token stays positional: `report --exclude-generated cov.xml` must not
    // swallow the path as the flag's value. Comparer matches the parsed dictionary's.
    static readonly HashSet<string> ValuelessFlags =
        new(StringComparer.OrdinalIgnoreCase) { "exclude-generated", "exclude-test-code", "probe-sources", "github-summary", "no-color" };

    /// <summary>
    /// A command's arguments as options, plus an error for each argument the dictionary
    /// misrepresents: an option filed as a path (a single-dash token) or under a path's slot
    /// (which would replace that path), and a flag that got no value (recorded as "true", so an
    /// unknown name can still be reported by name).
    /// </summary>
    sealed record ParsedArguments(
        Dictionary<string, string> Options, List<UsageError> Unknown, List<UsageError> WithoutValue);

    static ParsedArguments Parse(string command, string[] raw)
    {
        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unknown = new List<UsageError>();
        var withoutValue = new List<UsageError>();
        var positionals = Positionals(command);
        string? pendingKey = null;
        var positional = 0;

        for (var i = 1; i < raw.Length; i++)
        {
            if (raw[i].StartsWith("--"))
            {
                if (pendingKey is not null) EndWithoutValue(pendingKey);
                var key = raw[i][2..];
                if (IsPathSlot(command, key))
                    unknown.Add(UnknownOptionError(command, raw[i], key, "Pass the path as a positional argument."));

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
                if (raw[i].StartsWith('-'))
                    unknown.Add(UnknownOptionError(command, raw[i], raw[i].TrimStart('-'),
                        command is "test" ? "Pass dotnet test arguments after '--'." : null));
                parsed[positional < positionals.Length ? positionals[positional] : $"arg{positional}"] = raw[i];
                positional++;
            }
        }

        if (pendingKey is not null) EndWithoutValue(pendingKey);

        return new ParsedArguments(parsed, unknown, withoutValue);

        void EndWithoutValue(string key)
        {
            // Its "true" placeholder is no value either: it would stamp a snapshot "true" or scan
            // a directory for a file named "true".
            parsed[key] = "true";
            withoutValue.Add(new UsageError($"Option '--{key}' requires a value."));
        }
    }

    /// <summary>The dictionary keys a command's positional arguments are filed under, in order.</summary>
    static string[] Positionals(string command) => command switch
    {
        "diff" => ["before", "after"],
        "help" => ["command"],
        "version" => [],
        _ => ["file"]
    };

    /// <summary>The positional slots that hold a path: every one but the command help names. None of them is a flag.</summary>
    static bool IsPathSlot(string command, string key) =>
        command is not "help" && Positionals(command).Contains(key, StringComparer.OrdinalIgnoreCase);
}
