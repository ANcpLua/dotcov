using DotCov.Tool;

namespace DotCov.Tests;
/// <summary>
/// Help reachable from anywhere: `dotcov &lt;command&gt; --help`, `-h`, and `dotcov help &lt;command&gt;`
/// print that command's section, and the full help is built from the same sections. Driven
/// through <see cref = "DotCovCli.RunAsync"/>.
/// </summary>
[TestClass]
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
    /// <summary>The block under <paramref name = "heading"/>, up to the next blank line.</summary>
    private static string[] Block(string help, string heading) => [..Lines(help).SkipWhile(l => l != heading).Skip(1).TakeWhile(static l => l.Length > 0)];
    /// <summary>The options a help section lists, by name.</summary>
    private static string[] OptionNames(string help) => [..Block(help, "Options:").Where(static l => l.StartsWith("  --", StringComparison.Ordinal)).Select(static l => l[2..].Split(' ', ',')[0])];
    [TestMethod]
    [DynamicData(nameof(EveryCommand))]
    public async Task EveryForm_PrintsTheSameSection_ToStdout_Exit0(string command)
    {
        var forms = new[]
        {
            new[]
            {
                command,
                "--help"
            },
            [command, "-h"],
            ["help", command],
            ["--help", command],
            ["-h", command]
        };
        var(code, section, stderr) = await Run(forms[0]);
        Assert.AreEqual(0, code);
        Assert.IsEmpty(stderr);
        Assert.StartsWith($"dotcov {command}", section);
        foreach (var form in forms.Skip(1))
        {
            var run = await Run(form);
            Assert.AreEqual(0, run.Code, message: string.Join(' ', form));
            Assert.AreEqual(section, run.StdOut, message: string.Join(' ', form));
            Assert.IsEmpty(run.StdErr);
        }
    }

    [TestMethod]
    [DynamicData(nameof(EveryCommand))]
    public async Task Section_IsOnlyThatCommand(string command)
    {
        var(_, section, _) = await Run(command, "--help");
        var(_, full, _) = await Run("help");
        Assert.IsLessThan(full.Length, section.Length);
        Assert.DoesNotContain("Commands:", section);
        // No other command's usage line: each starts a line with "dotcov <name> ".
        foreach (var other in Commands.Where(c => c != command))
            Assert.DoesNotContain(l => l.StartsWith($"dotcov {other} ", StringComparison.Ordinal), Lines(section), message: other);
        Assert.IsNotEmpty(Block(section, "Exit codes:"));
    }

    [TestMethod]
    [DataRow("report", "0 2 5")]
    [DataRow("check", "0 1 2 3 4 5")]
    [DataRow("crap", "0 1 2 3 4 5")]
    [DataRow("diff", "0 2 5")]
    [DataRow("snapshot", "0 2 5")]
    [DataRow("test", "0 1 2 3 4 5 6")]
    [DataRow("version", "0 2")]
    [DataRow("help", "0 2")]
    public async Task Section_ShowsTheExitCodesThatApply_AsTheFullTableWordsThem(string command, string codes)
    {
        var(_, section, _) = await Run("help", command);
        var(_, full, _) = await Run("help");
        var rows = Block(section, "Exit codes:");
        var shown = rows.Where(static r => !r.StartsWith("    ", StringComparison.Ordinal)).Select(static r => r.TrimStart()[..1]);
        Assert.AreEqual(codes, string.Join(' ', shown));
        // One table: a section's rows are the full table's rows, word for word.
        var table = Block(full, "Exit codes:");
        foreach (var row in rows)
            Assert.Contains(row, table);
    }

    [TestMethod]
    [DynamicData(nameof(EveryCommand))]
    public async Task Section_ListsExactlyTheOptionsTheCommandAccepts(string command)
    {
        // The error for an unknown option lists what the command accepts; help must list the same.
        var(_, section, _) = await Run(command, "--help");
        var(code, _, stderr) = await Run(command, "--no-such-option");
        Assert.AreEqual(2, code);
        var accepted = Lines(stderr)[1][$"valid options for '{command}': ".Length..].Split(", ");
        Assert.AreSequenceEqual(accepted, OptionNames(section), SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    public async Task FullHelp_IsBuiltFromTheSections()
    {
        var(code, full, stderr) = await Run();
        Assert.AreEqual(0, code);
        Assert.IsEmpty(stderr);
        Assert.AreEqual("dotcov - Cobertura coverage toolkit", Lines(full)[0]);
        Assert.Contains("dotcov <command> --help", full);
        var commands = Block(full, "Commands:");
        var options = Block(full, "Options (a command rejects any option it does not take, never ignores it):");
        foreach (var command in Commands)
        {
            var(_, section, _) = await Run(command, "--help");
            var synopsis = Lines(section).TakeWhile(static l => l.Length > 0).ToArray();
            // The command's usage and summary, indented under Commands:.
            foreach (var line in synopsis)
                Assert.Contains("  " + line, commands, message: command);
            // Each option's first line, and every paragraph of the section, appear once in the full help.
            foreach (var option in Block(section, "Options:").Where(static l => l.StartsWith("  --", StringComparison.Ordinal)))
                Assert.Contains(option, options, message: command);
            foreach (var paragraph in section.Split(Environment.NewLine + Environment.NewLine).Skip(2).Where(static p => !p.StartsWith("Exit codes:", StringComparison.Ordinal) && !p.StartsWith("Examples:", StringComparison.Ordinal)))
                Assert.Contains(paragraph.TrimEnd(), full, message: command);
        }
    }

    [TestMethod]
    public async Task FullHelp_NamesTheCommandsEachOptionBelongsTo()
    {
        var(_, full, _) = await Run("--help");
        var options = Lines(full);
        var format = Array.FindIndex(options, static l => l.StartsWith("  --format ", StringComparison.Ordinal));
        var help = Array.FindIndex(options, static l => l.StartsWith("  --help, -h ", StringComparison.Ordinal));
        Assert.AreEqual("(report, check, crap, diff, test)", options[format + 1].Trim());
        Assert.AreEqual("(every command)", options[help + 1].Trim());
    }

    [TestMethod]
    public async Task HelpFlag_WinsOverEveryOtherArgument()
    {
        // A broken invocation plus --help is a request for help, not for the error.
        var(code, stdout, stderr) = await Run("check", "missing.xml", "--min-line", "abc", "--bogus", "--help");
        var(_, section, _) = await Run("check", "--help");
        Assert.AreEqual(0, code);
        Assert.AreEqual(section, stdout);
        Assert.IsEmpty(stderr);
    }

    [TestMethod]
    public async Task Test_Help_RunsNoTests()
    {
        // In-process, a started run would echo "> dotnet test" first; help answers before that.
        var(code, stdout, stderr) = await Run("test", "--min-line", "80", "-h");
        Assert.AreEqual(0, code);
        Assert.StartsWith("dotcov test [<project>] --min-line N and/or --min-branch N", stdout);
        Assert.Contains("6  test run failed:", stdout);
        Assert.IsEmpty(stderr);
    }

    [TestMethod]
    public async Task Help_ForAnUnknownCommand_SuggestsTheClosest()
    {
        var(code, stdout, stderr) = await Run("help", "chek");
        Assert.AreEqual(2, code);
        Assert.IsEmpty(stdout);
        Assert.StartsWith($"error: Unknown command 'chek'. Did you mean 'check'?{Environment.NewLine}commands: ", stderr);
    }

    [TestMethod]
    public async Task Help_ForAnEmptyName_IsAnUnknownCommand()
    {
        var(code, stdout, stderr) = await Run("help", "");
        Assert.AreEqual(2, code);
        Assert.IsEmpty(stdout);
        Assert.StartsWith($"error: Unknown command ''.{Environment.NewLine}", stderr);
    }

    [TestMethod]
    [DataRow("help check report")]
    [DataRow("version extra")]
    public async Task CommandsWithoutAPath_RejectAnExtraArgument_WithoutMergeAdvice(string invocation)
    {
        var args = invocation.Split(' ');
        var(code, stdout, stderr) = await Run(args);
        Assert.AreEqual(2, code);
        Assert.IsEmpty(stdout);
        Assert.AreEqual($"error: Unexpected argument '{args[^1]}' for '{args[0]}'.{Environment.NewLine}", stderr);
    }

    [TestMethod]
    public async Task Help_UnknownOption_ListsHelpAsItsOnlyOption()
    {
        var(code, _, stderr) = await Run("help", "--verbose");
        Assert.AreEqual(2, code);
        Assert.AreEqual($"error: Unknown option '--verbose' for 'help'.{Environment.NewLine}valid options for 'help': --help{Environment.NewLine}", stderr);
    }

    [TestMethod]
    public async Task MissingPath_PointsAtTheCommandsHelp()
    {
        var(code, _, stderr) = await Run("crap");
        Assert.AreEqual(2, code);
        Assert.AreEqual($"error: missing path. Usage: dotcov crap <path> [options] (dotcov crap --help){Environment.NewLine}", stderr);
    }
}
