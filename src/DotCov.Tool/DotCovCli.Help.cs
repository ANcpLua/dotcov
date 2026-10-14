using System.Text;

namespace DotCov.Tool;

/// <summary>
/// The help text as data: every command, option, note, exit code, and example knows which
/// commands it belongs to. `dotcov help` renders all of it once; `dotcov &lt;command&gt; --help`
/// renders the parts that apply to one command. The same catalog decides which options a command
/// accepts and what an unknown option or command is corrected to, so help, parsing, and error
/// advice cannot drift apart.
/// </summary>
public static partial class DotCovCli
{
    /// <summary>One command: how it is invoked, what it does, and the options it takes, in help order.</summary>
    sealed record CommandHelp(string Name, string Usage, string[] Summary, string[] Options);

    /// <summary>One option: its name as parsed (no dashes), its syntax, and its description lines.</summary>
    sealed record OptionHelp(string Name, string Syntax, string[] Description);

    /// <summary>A paragraph, an exit code, or an example, shown in full help and in each listed command's help.</summary>
    sealed record HelpEntry(string[] Commands, string[] Lines);

    // Option groups several commands share: the exclusion flags and the file-identity flags.
    static readonly string[] Exclusion = ["exclude-generated", "exclude-test-code", "exclude-external", "keep"];
    static readonly string[] Identities = ["probe-sources", "source-root"];

    static readonly CommandHelp[] Commands =
    [
        new("report", "dotcov report <path> [options]",
            ["Parse and display coverage."],
            ["format", "top", "threshold", "no-color", .. Exclusion, .. Identities, "pattern", "max-chars", "upload", "github-summary"]),
        new("check", "dotcov check <path> --min-line N and/or --min-branch N [options]",
            ["CI gate: exit 1 when line or branch coverage is below its minimum."],
            ["min-line", "min-branch", "format", "top", "no-color", .. Exclusion, .. Identities, "pattern", "max-chars", "upload", "github-summary", "badge-dir"]),
        new("crap", "dotcov crap <path> [options]",
            ["CRAP gate, comp^2*(1-cov)^3+comp per method: exit 1 when any method scores strictly",
             "above --max-crap (at-threshold passes; default 30; every method must be scored)."],
            ["max-crap", "top", "metrics", "format", "no-color", .. Exclusion, .. Identities, "pattern", "max-chars", "github-summary"]),
        new("diff", "dotcov diff <before> <after> [options]",
            ["Compare two reports: the files whose coverage changed, and a count of the rest."],
            ["format", "no-color", .. Exclusion, .. Identities, "pattern", "max-chars"]),
        new("snapshot", "dotcov snapshot <path> [options]",
            ["Create pipeline-ready JSON (identity defaults to 'unknown')."],
            ["commit", "branch", "project", "no-color", .. Exclusion, .. Identities, "pattern", "max-chars", "upload"]),
        new("test", "dotcov test [<project>] --min-line N and/or --min-branch N [options] [-- <dotnet test arguments>]",
            ["Run dotnet test with coverage into a fresh TestResults/<run>, then report and gate it",
             "like check."],
            ["min-line", "min-branch", "format", "top", "no-color", .. Exclusion, .. Identities, "github-summary", "badge-dir"]),
        new("version", "dotcov version, dotcov --version",
            ["Show the version."],
            []),
        new("help", "dotcov help [<command>], dotcov <command> --help",
            ["Show this help, or one command's usage, options, and exit codes."],
            []),
    ];

    static readonly Dictionary<string, CommandHelp> CommandsByName =
        Commands.ToDictionary(static c => c.Name, StringComparer.Ordinal);

    // What each command parses: the options its help lists and its positional slots. Anything
    // else — a misspelled flag, '--name=value', a flag only another command reads, a second path
    // from a shell glob — is rejected, not ignored, so a gate cannot pass on defaults nobody asked
    // for. Declared after Commands: static initializers of one file run in textual order.
    static readonly Dictionary<string, HashSet<string>> AcceptedOptions = Commands.ToDictionary(
        static c => c.Name,
        static c => new HashSet<string>([.. c.Options, .. Positionals(c.Name)], StringComparer.OrdinalIgnoreCase),
        StringComparer.Ordinal);

    static readonly OptionHelp[] Options =
    [
        new("format", "--format table|json|md", ["Output format (default table; markdown is the long name of md)"]),
        new("threshold", "--threshold N", ["Minimum the markdown verdict shows, 0 to 100; display only, never", "the exit code"]),
        new("min-line", "--min-line N", ["Minimum line coverage, 0 to 100; 0 disables the dimension"]),
        new("min-branch", "--min-branch N", ["Minimum branch coverage, 0 to 100; 0 disables the dimension"]),
        new("max-crap", "--max-crap N", ["Highest CRAP score that passes (default 30)"]),
        new("top", "--top N",
            ["Show the N worst files (report) or methods (crap), or list at most N files below a",
             "threshold (check, test; default 20); a gate still evaluates everything"]),
        new("metrics", "--metrics <file>", ["Cyclomatic complexity from `dotnet msbuild /t:Metrics`, for emitters", "whose coverage XML does not carry it"]),
        new("commit", "--commit <sha>", ["Commit the snapshot is stamped with (default 'unknown', warned)"]),
        new("branch", "--branch <name>", ["Branch the snapshot is stamped with (default 'unknown', warned)"]),
        new("project", "--project <name>", ["Project the snapshot is stamped with (default 'unknown', warned)"]),
        new("exclude-generated", "--exclude-generated", ["Skip generated files, migrations, GlobalUsings.cs, Program.cs"]),
        new("exclude-test-code", "--exclude-test-code", ["Skip test projects and fixtures: *.Tests/, TestSupport/, TestFixtures/"]),
        new("exclude-external", "--exclude-external",
            ["Skip code built elsewhere: SourceLink paths (/_/...) of dependencies the coverage",
             "tool instrumented; a warning names them when this flag is off"]),
        new("keep", "--keep <substrings>",
            ["Exempt comma-separated paths from the exclusions by case-insensitive",
             "substring match, not globs; requires --exclude-generated,",
             "--exclude-test-code, or --exclude-external (e.g. --keep Program.cs",
             "measures a CLI tool's entry point)"]),
        new("probe-sources", "--probe-sources",
            ["Resolve a relative filename under several <source> roots against the",
             "root that holds it on this machine's disk; the result depends on where",
             "dotcov runs"]),
        new("source-root", "--source-root <path>", ["Resolve those filenames against this declared root instead"]),
        new("pattern", "--pattern <glob>",
            ["Report filename to scan directories for: 'filename' (top level only)",
             $"or '**/filename' (recursive) (default {ReportPattern.DefaultText})"]),
        new("max-chars", "--max-chars <N>", [$"Per-file XML character cap (default {DefaultMaxChars}; 0 = no cap)"]),
        new("upload", "--upload <url>", ["POST the JSON payload to an http(s) endpoint"]),
        new("github-summary", "--github-summary", ["Append markdown to the file $GITHUB_STEP_SUMMARY names"]),
        new("badge-dir", "--badge-dir <dir>",
            ["Write the verdict as coverage-badge.svg and coverage-badge.json into",
             "<dir>, created if missing, for every outcome"]),
        new("no-color", "--no-color",
            ["Plain output without ANSI colour, as NO_COLOR=1 gives; accepted by every command that",
             "reads a report, so a script can pass it everywhere"]),
    ];

    static readonly Dictionary<string, OptionHelp> OptionsByName =
        Options.ToDictionary(static o => o.Name, StringComparer.Ordinal);

    // Every command takes --help; it is answered before the other arguments are parsed.
    static readonly OptionHelp HelpOption = new("help", "--help, -h", ["Show one command's usage, options, and exit codes"]);

    static readonly string[] AllCommands = [.. Commands.Select(static c => c.Name)];
    static readonly string[] PathCommands = ["report", "check", "crap", "diff", "snapshot"];
    static readonly string[] ReportCommands = [.. PathCommands, "test"];
    static readonly string[] GateCommands = ["check", "crap", "test"];
    static readonly string[] ColorCommands = ["report", "crap", "diff", "test"];
    static readonly string[] ExclusionCommands = ["report", "check", "crap", "diff", "snapshot", "test"];

    static readonly HelpEntry[] Notes =
    [
        new(ReportCommands,
            ["Output: stdout carries only the command's data - the table, markdown, or JSON. The verdict",
             "line of check, crap, and test (PASS, FAIL, NODATA, DISABLED) is the first stderr line, in",
             "every format; warnings, errors, and the merged reports follow it."]),
        new(["check", "test"],
            ["Thresholds are percentages from 0 to 100, and there is no default: pass --min-line,",
             "--min-branch, or both. A dimension left out is not checked; 0 disables one. Without",
             "either, check and test exit 2 before reading a report or running a test."]),
        new(PathCommands,
            [$"<path> can be a file or directory. Directories are scanned for {ReportPattern.DefaultText};",
             "override the filename with --pattern (gcovr and coverage.py emit coverage.xml). A directory",
             "without a match gets a stderr warning naming the .xml files it holds. Every match is merged;",
             "when there is more than one, stderr lists them after the result. Give each test run a fresh",
             "results directory so an earlier run's report is not merged."]),
        new(ExclusionCommands,
            ["With an exclusion active, stderr lists its rules and every excluded file once; JSON",
             "output carries them as \"excluded\" and \"exclusionRules\"."]),
        new(ReportCommands,
            ["JSON output starts with \"schemaVersion\": 1, \"command\", \"inputs\" (the merged reports), and",
             "\"warnings\". Uncovered lines are range strings such as \"21,23-24\". check, test, and crap add a",
             "\"gate\" object: outcome, exitCode, reason (the verdict line), and the thresholds; check and",
             "test also list \"offenders\" with their uncovered lines and partially covered branches."]),
        new(["check", "test"],
            ["A FAIL lists every file below a failed threshold, worst first, with its uncovered line",
             "ranges and partially covered branch lines - stderr for the table, in the document for",
             "json and md. --top N caps the list (default 20)."]),
        new(ReportCommands,
            ["A report that declares several <source> roots does not say which root a relative",
             "filename belongs to. By default dotcov never guesses from the disk: the file is keyed",
             "under the first root with a warning, report prints as usual, and check, crap, and test",
             "exit 3 (NODATA) until --probe-sources or --source-root resolves it."]),
        new(["crap"],
            ["crap needs cyclomatic complexity for every method: coverlet embeds it in the coverage XML",
             "(used automatically); for other emitters pass --metrics <file> produced by",
             "`dotnet msbuild /t:Metrics` (Microsoft.CodeAnalysis.Metrics package). A method without",
             "it is listed as unscored, and the gate exits 3 rather than vouch for code it did not score.",
             "The table's Location column gives each method's file:line."]),
        new(["diff"],
            ["diff lists the files that were added, removed, or whose line rate moved, then counts the",
             "unchanged files on one line; JSON lists every file with its change."]),
        new(["test"],
            ["test passes the coverage options of the runner dotnet test uses in this directory",
             "(DOTNET_TEST_RUNNER, else global.json test.runner, else VSTest). MTP test projects need",
             "Microsoft.Testing.Extensions.CodeCoverage; VSTest projects get the Code Coverage collector",
             "from Microsoft.NET.Test.Sdk. dotnet test output goes to stdout ahead of the table; with",
             "--format json or md it goes to TestResults/<run>/dotnet-test.log and reaches stderr only",
             "if the run fails. After the verdict, stderr names the run's reports as",
             "'reports: TestResults/<run>'. A failed run exits 6 without evaluating coverage."]),
        new(ColorCommands,
            ["Tables are coloured on a terminal and in CI logs that render ANSI (GitHub Actions, GitLab,",
             "Azure Pipelines, ...). --no-color or NO_COLOR=1 turns colour off; test also passes",
             "NO_COLOR=1 to dotnet test."]),
    ];

    /// <summary>The one exit-code table: full help shows every row, a command's help the rows that apply to it.</summary>
    static readonly HelpEntry[] ExitCodeEntries =
    [
        new(AllCommands, ["0  success; check, crap, and test require a measured pass (JSON: gate.exitCode)"]),
        new(GateCommands, ["1  FAIL: below a threshold"]),
        new(AllCommands,
            ["2  usage error: unknown command or option, invalid value, missing or extra argument,",
             "   no threshold (check, test), a --source-root no report declares"]),
        new(GateCommands, ["3  NODATA: missing measurements, an unscored CRAP method, an ambiguous file identity"]),
        new(GateCommands, ["4  DISABLED: no active threshold"]),
        new(ReportCommands,
            ["5  input/IO error: supplied path unavailable, parse/IO/size-cap error, upload or badge",
             "   write failure, dotnet could not be started (test); a non-passing gate keeps its own",
             "   exit code even if an upload or badge write also fails"]),
        new(["test"], ["6  test run failed: dotnet test exited nonzero; coverage was not evaluated"]),
    ];

    static readonly HelpEntry[] Examples =
    [
        new(["report"], ["dotcov report TestResults/"]),
        new(["report"], ["dotcov report coverage.cobertura.xml --format json --exclude-generated > coverage.json"]),
        new(["check"], ["dotcov check TestResults/ --min-line 80 --exclude-generated --github-summary"]),
        new(["check"], ["dotcov check TestResults/ --min-line 90 --min-branch 80 --badge-dir badge"]),
        new(["check"], ["dotcov check TestResults/ --min-branch 75 --exclude-test-code"]),
        new(["check"], ["dotcov check TestResults/ --min-line 90 --format json   # verdict, offenders, ranges as JSON"]),
        new(["report"], ["dotcov report TestResults/ --top 10   # the ten worst files"]),
        new(["crap"], ["dotcov crap TestResults/ --max-crap 6 --github-summary"]),
        new(["crap"], ["dotcov crap coverage.cobertura.xml --metrics MyApp.Metrics.xml --top 10 --no-color"]),
        new(["report"], ["dotcov report gcovr-output/ --pattern \"**/coverage.xml\"   # non-Coverlet report names"]),
        new(["check"], ["dotcov check coverage.xml --min-line 85 --source-root src   # several <source> roots"]),
        new(["report"], ["dotcov report TestResults/ --exclude-generated --keep Program.cs   # measure host bootstrap"]),
        new(["snapshot"], ["dotcov snapshot TestResults/ --commit abc123 --branch main --project MyApp --upload https://qyl/api/v1/coverage"]),
        new(["diff"], ["dotcov diff before.cobertura.xml after.cobertura.xml --format md"]),
        new(["test"], ["dotcov test tests/MyApp.Tests --min-line 80 --exclude-generated -- -c Release"]),
        new(["help"], ["dotcov crap --help"]),
        new(["help"], ["dotcov help diff"]),
        new(["version"], ["dotcov --version"]),
    ];

    /// <summary>`dotcov help [&lt;command&gt;]`: the full help, or one command's.</summary>
    static int Help(Dictionary<string, string> opts, TextWriter stdout)
    {
        if (!opts.TryGetValue("command", out var name))
        {
            stdout.Write(FullHelp());
            return 0;
        }

        stdout.Write(HelpFor(CommandsByName.GetValueOrDefault(name) ?? throw UnknownCommand(name)));
        return 0;
    }

    static string FullHelp()
    {
        var sb = new StringBuilder();
        sb.AppendLine("dotcov - Cobertura coverage toolkit");
        sb.AppendLine();
        sb.AppendLine("One command's usage, options, and exit codes: dotcov <command> --help, or dotcov help <command>");
        sb.AppendLine();
        sb.AppendLine("Commands:");
        foreach (var command in Commands)
            AppendSynopsis(sb, command, "  ");

        sb.AppendLine();
        sb.AppendLine("Options (a command rejects any option it does not take, never ignores it):");
        foreach (var option in Options)
            AppendOption(sb, option, $"({string.Join(", ", Commands.Where(c => c.Options.Contains(option.Name)).Select(static c => c.Name))})");
        AppendOption(sb, HelpOption, "(every command)");

        foreach (var note in Notes)
        {
            sb.AppendLine();
            AppendLines(sb, note.Lines, "");
        }

        AppendEntries(sb, "Exit codes:", ExitCodeEntries);
        AppendEntries(sb, "Examples:", Examples);
        return sb.ToString();
    }

    static string HelpFor(CommandHelp command)
    {
        var sb = new StringBuilder();
        AppendSynopsis(sb, command, "");

        sb.AppendLine();
        sb.AppendLine("Options:");
        foreach (var name in command.Options)
            AppendOption(sb, OptionsByName[name], null);
        AppendOption(sb, HelpOption, null);

        foreach (var note in Notes)
        {
            if (!note.Commands.Contains(command.Name)) continue;
            sb.AppendLine();
            AppendLines(sb, note.Lines, "");
        }

        AppendEntries(sb, "Exit codes:", ExitCodeEntries.Where(e => e.Commands.Contains(command.Name)));
        AppendEntries(sb, "Examples:", Examples.Where(e => e.Commands.Contains(command.Name)));
        return sb.ToString();
    }

    static void AppendSynopsis(StringBuilder sb, CommandHelp command, string indent)
    {
        sb.AppendLine(indent + command.Usage);
        AppendLines(sb, command.Summary, indent + "    ");
    }

    // The syntax column is 24 wide; continuation lines and the command list align under the text.
    static void AppendOption(StringBuilder sb, OptionHelp option, string? commands)
    {
        sb.AppendLine($"  {option.Syntax,-24}  {option.Description[0]}");
        AppendLines(sb, [.. option.Description.Skip(1), .. commands is null ? [] : new[] { commands }], new string(' ', 28));
    }

    static void AppendEntries(StringBuilder sb, string heading, IEnumerable<HelpEntry> entries)
    {
        sb.AppendLine();
        sb.AppendLine(heading);
        foreach (var entry in entries)
            AppendLines(sb, entry.Lines, "  ");
    }

    static void AppendLines(StringBuilder sb, IEnumerable<string> lines, string indent)
    {
        foreach (var line in lines)
            sb.AppendLine(indent + line);
    }

    // ── Mistakes that name their fix ──

    /// <summary>The options a command's error advice lists and corrects to: its own, then --help.</summary>
    static IEnumerable<string> ValidOptions(string command) => CommandsByName[command].Options.Append(HelpOption.Name);

    /// <summary>A command name that is not one: the closest command, then every command.</summary>
    static UsageError UnknownCommand(string name) =>
        new($"Unknown command '{name}'.{DidYouMean(name, AllCommands, "")}{Environment.NewLine}" +
            $"commands: {string.Join(", ", AllCommands)}");

    /// <summary>
    /// <paramref name="token"/>, an option <paramref name="command"/> does not take: the option it
    /// takes closest to <paramref name="name"/> (none when null: the advice names the fix), the
    /// <paramref name="advice"/> for this kind of mistake, then every option it takes.
    /// </summary>
    static UsageError UnknownOptionError(string command, string token, string? name, string? advice = null)
    {
        var valid = ValidOptions(command).ToArray();
        return new UsageError(
            $"Unknown option '{token}' for '{command}'.{(name is null ? "" : DidYouMean(name, valid, "--"))}" +
            (advice is null ? "" : " " + advice) + Environment.NewLine +
            $"valid options for '{command}': {string.Join(", ", valid.Select(static o => "--" + o))}");
    }

    /// <summary>" Did you mean 'x'?" for the first candidate within edit distance 2, case-insensitively; empty otherwise.</summary>
    static string DidYouMean(string input, IEnumerable<string> candidates, string prefix) =>
        candidates.Select(c => (Name: c, Distance: EditDistance(input, c)))
            .Where(static c => c.Distance <= 2)
            .OrderBy(static c => c.Distance)
            .Select(c => $" Did you mean '{prefix}{c.Name}'?")
            .FirstOrDefault("");

    /// <summary>Levenshtein distance over lower-cased characters.</summary>
    static int EditDistance(string a, string b)
    {
        a = a.ToLowerInvariant();
        b = b.ToLowerInvariant();
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1], previous[j]) + 1, previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
