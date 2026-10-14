using DotCov.Tool;

namespace DotCov.Tests;

/// <summary>
/// Help reachable from anywhere: `dotcov &lt;command&gt; --help`, `-h`, and `dotcov help &lt;command&gt;`
/// print that command's section, and the full help is built from the same sections. Driven
/// through <see cref="DotCovCli.RunAsync"/>.
/// </summary>
public sealed class CliHelpTests
{
    private static readonly string[] Commands = ["report", "check", "crap", "diff", "snapshot", "test", "version", "help"];

    public static IEnumerable<string> EveryCommand() => Commands;

    private static async Task<(int Code, string StdOut, string StdErr)> Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await DotCovCli.RunAsync(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static string[] Lines(string text) => text.Split(Environment.NewLine);

    /// <summary>The block under <paramref name="heading"/>, up to the next blank line.</summary>
    private static string[] Block(string help, string heading) =>
        [.. Lines(help).SkipWhile(l => l != heading).Skip(1).TakeWhile(static l => l.Length > 0)];

    /// <summary>The options a help section lists, by name.</summary>
    private static string[] OptionNames(string help) =>
        [.. Block(help, "Options:").Where(static l => l.StartsWith("  --", StringComparison.Ordinal))
            .Select(static l => l[2..].Split(' ', ',')[0])];

    [Test]
    [MethodDataSource(nameof(EveryCommand))]
    public async Task EveryForm_PrintsTheSameSection_ToStdout_Exit0(string command)
    {
        var forms = new[]
        {
            new[] { command, "--help" }, [command, "-h"], ["help", command], ["--help", command], ["-h", command]
        };

        var (code, section, stderr) = await Run(forms[0]);

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stderr).IsEmpty();
        await Assert.That(section).StartsWith($"dotcov {command}");
        foreach (var form in forms.Skip(1))
        {
            var run = await Run(form);
            await Assert.That(run.Code).IsEqualTo(0).Because(string.Join(' ', form));
            await Assert.That(run.StdOut).IsEqualTo(section).Because(string.Join(' ', form));
            await Assert.That(run.StdErr).IsEmpty();
        }
    }

    [Test]
    [MethodDataSource(nameof(EveryCommand))]
    public async Task Section_IsOnlyThatCommand(string command)
    {
        var (_, section, _) = await Run(command, "--help");
        var (_, full, _) = await Run("help");

        await Assert.That(section.Length).IsLessThan(full.Length);
        await Assert.That(section).DoesNotContain("Commands:");
        // No other command's usage line: each starts a line with "dotcov <name> ".
        foreach (var other in Commands.Where(c => c != command))
            await Assert.That(Lines(section).Any(l => l.StartsWith($"dotcov {other} ", StringComparison.Ordinal))).IsFalse().Because(other);
        await Assert.That(Block(section, "Exit codes:")).IsNotEmpty();
    }

    [Test]
    [Arguments("report", "0 2 5")]
    [Arguments("check", "0 1 2 3 4 5")]
    [Arguments("crap", "0 1 2 3 4 5")]
    [Arguments("diff", "0 2 5")]
    [Arguments("snapshot", "0 2 5")]
    [Arguments("test", "0 1 2 3 4 5 6")]
    [Arguments("version", "0 2")]
    [Arguments("help", "0 2")]
    public async Task Section_ShowsTheExitCodesThatApply_AsTheFullTableWordsThem(string command, string codes)
    {
        var (_, section, _) = await Run("help", command);
        var (_, full, _) = await Run("help");

        var rows = Block(section, "Exit codes:");
        var shown = rows.Where(static r => !r.StartsWith("    ", StringComparison.Ordinal)).Select(static r => r.TrimStart()[..1]);
        await Assert.That(string.Join(' ', shown)).IsEqualTo(codes);
        // One table: a section's rows are the full table's rows, word for word.
        var table = Block(full, "Exit codes:");
        foreach (var row in rows)
            await Assert.That(table).Contains(row);
    }

    [Test]
    [MethodDataSource(nameof(EveryCommand))]
    public async Task Section_ListsExactlyTheOptionsTheCommandAccepts(string command)
    {
        // The error for an unknown option lists what the command accepts; help must list the same.
        var (_, section, _) = await Run(command, "--help");
        var (code, _, stderr) = await Run(command, "--no-such-option");

        await Assert.That(code).IsEqualTo(2);
        var accepted = Lines(stderr)[1][$"valid options for '{command}': ".Length..].Split(", ");
        await Assert.That(OptionNames(section)).IsEquivalentTo(accepted);
    }

    [Test]
    public async Task FullHelp_IsBuiltFromTheSections()
    {
        var (code, full, stderr) = await Run();

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stderr).IsEmpty();
        await Assert.That(Lines(full)[0]).IsEqualTo("dotcov - Cobertura coverage toolkit");
        await Assert.That(full).Contains("dotcov <command> --help");
        var commands = Block(full, "Commands:");
        var options = Block(full, "Options (a command rejects any option it does not take, never ignores it):");
        foreach (var command in Commands)
        {
            var (_, section, _) = await Run(command, "--help");
            var synopsis = Lines(section).TakeWhile(static l => l.Length > 0).ToArray();
            // The command's usage and summary, indented under Commands:.
            foreach (var line in synopsis)
                await Assert.That(commands).Contains("  " + line).Because(command);
            // Each option's first line, and every paragraph of the section, appear once in the full help.
            foreach (var option in Block(section, "Options:").Where(static l => l.StartsWith("  --", StringComparison.Ordinal)))
                await Assert.That(options).Contains(option).Because(command);
            foreach (var paragraph in section.Split(Environment.NewLine + Environment.NewLine).Skip(2)
                         .Where(static p => !p.StartsWith("Exit codes:", StringComparison.Ordinal) && !p.StartsWith("Examples:", StringComparison.Ordinal)))
                await Assert.That(full).Contains(paragraph.TrimEnd()).Because(command);
        }
    }

    [Test]
    public async Task FullHelp_NamesTheCommandsEachOptionBelongsTo()
    {
        var (_, full, _) = await Run("--help");

        var options = Lines(full);
        var format = Array.FindIndex(options, static l => l.StartsWith("  --format ", StringComparison.Ordinal));
        var help = Array.FindIndex(options, static l => l.StartsWith("  --help, -h ", StringComparison.Ordinal));
        await Assert.That(options[format + 1].Trim()).IsEqualTo("(report, check, crap, diff, test)");
        await Assert.That(options[help + 1].Trim()).IsEqualTo("(every command)");
    }

    [Test]
    public async Task HelpFlag_WinsOverEveryOtherArgument()
    {
        // A broken invocation plus --help is a request for help, not for the error.
        var (code, stdout, stderr) = await Run("check", "missing.xml", "--min-line", "abc", "--bogus", "--help");
        var (_, section, _) = await Run("check", "--help");

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stdout).IsEqualTo(section);
        await Assert.That(stderr).IsEmpty();
    }

    [Test]
    public async Task Test_Help_RunsNoTests()
    {
        // In-process, a started run would echo "> dotnet test" first; help answers before that.
        var (code, stdout, stderr) = await Run("test", "--min-line", "80", "-h");

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stdout).StartsWith("dotcov test [<project>] --min-line N and/or --min-branch N");
        await Assert.That(stdout).Contains("6  test run failed:");
        await Assert.That(stderr).IsEmpty();
    }

    [Test]
    public async Task Help_ForAnUnknownCommand_SuggestsTheClosest()
    {
        var (code, stdout, stderr) = await Run("help", "chek");

        await Assert.That(code).IsEqualTo(2);
        await Assert.That(stdout).IsEmpty();
        await Assert.That(stderr).StartsWith($"error: Unknown command 'chek'. Did you mean 'check'?{Environment.NewLine}commands: ");
    }

    [Test]
    public async Task Help_ForAnEmptyName_IsAnUnknownCommand()
    {
        var (code, stdout, stderr) = await Run("help", "");

        await Assert.That(code).IsEqualTo(2);
        await Assert.That(stdout).IsEmpty();
        await Assert.That(stderr).StartsWith($"error: Unknown command ''.{Environment.NewLine}");
    }

    [Test]
    [Arguments("help check report")]
    [Arguments("version extra")]
    public async Task CommandsWithoutAPath_RejectAnExtraArgument_WithoutMergeAdvice(string invocation)
    {
        var args = invocation.Split(' ');

        var (code, stdout, stderr) = await Run(args);

        await Assert.That(code).IsEqualTo(2);
        await Assert.That(stdout).IsEmpty();
        await Assert.That(stderr).IsEqualTo($"error: Unexpected argument '{args[^1]}' for '{args[0]}'.{Environment.NewLine}");
    }

    [Test]
    public async Task Help_UnknownOption_ListsHelpAsItsOnlyOption()
    {
        var (code, _, stderr) = await Run("help", "--verbose");

        await Assert.That(code).IsEqualTo(2);
        await Assert.That(stderr).IsEqualTo(
            $"error: Unknown option '--verbose' for 'help'.{Environment.NewLine}valid options for 'help': --help{Environment.NewLine}");
    }

    [Test]
    public async Task MissingPath_PointsAtTheCommandsHelp()
    {
        var (code, _, stderr) = await Run("crap");

        await Assert.That(code).IsEqualTo(2);
        await Assert.That(stderr).IsEqualTo(
            $"error: missing path. Usage: dotcov crap <path> [options] (dotcov crap --help){Environment.NewLine}");
    }
}
