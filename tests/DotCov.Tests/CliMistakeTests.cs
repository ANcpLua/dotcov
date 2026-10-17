using DotCov.Tool;

namespace DotCov.Tests;
/// <summary>
/// Mistakes that tell you the fix: an unknown option names the command, suggests the option
/// within two edits, and lists every option the command takes, on two stderr lines with exit 2
/// and nothing on stdout. Each is rejected before any report is read or test is run.
/// </summary>
[TestClass]
public sealed class CliMistakeTests(TestContext testContext)
{
    private static async Task<(int Code, string StdOut, string StdErr)> Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await DotCovCli.RunAsync(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private const string ReportOptions = "valid options for 'report': --format, --top, --threshold, --no-color, --exclude-generated, --exclude-test-code, " + "--exclude-external, --keep, --probe-sources, --source-root, --pattern, --max-chars, --upload, --github-summary, --help";
    private static void AssertTwoLineError(int code, string stdout, string stderr, string first, string second)
    {
        Assert.AreEqual(DotCovCli.UsageExitCode, code);
        Assert.IsEmpty(stdout);
        Assert.AreEqual($"error: {first}{Environment.NewLine}{second}{Environment.NewLine}", stderr);
    }

    [TestMethod]
    [DataRow("report missing.xml --fromat json", "--fromat", "--format")]
    [DataRow("check missing.xml --min-lin 80", "--min-lin", "--min-line")]
    [DataRow("crap missing.xml --max-crp 6", "--max-crp", "--max-crap")]
    [DataRow("diff a.xml b.xml --patern x.xml", "--patern", "--pattern")]
    [DataRow("snapshot missing.xml --comit abc", "--comit", "--commit")]
    [DataRow("report missing.xml --no-colour", "--no-colour", "--no-color")]
    [DataRow("check missing.xml --min-line 80 --badgedir out", "--badgedir", "--badge-dir")]
    [DataRow("report missing.xml --EXCLUDE-GENERATD", "--EXCLUDE-GENERATD", "--exclude-generated")]
    public async Task UnknownOption_WithinTwoEdits_IsNamed_AndEveryValidOptionListed(string invocation, string typed, string meant)
    {
        var args = invocation.Split(' ');
        var(code, stdout, stderr) = await Run(args);
        var lines = stderr.Split(Environment.NewLine);
        Assert.AreEqual(2, code);
        Assert.IsEmpty(stdout);
        Assert.HasCount(3, lines); // two lines and the final newline
        Assert.AreEqual($"error: Unknown option '{typed}' for '{args[0]}'. Did you mean '{meant}'?", lines[0]);
        var prefix = $"valid options for '{args[0]}': ";
        Assert.StartsWith(prefix, lines[1]);
        Assert.Contains(meant, lines[1][prefix.Length..].Split(", "));
    }

    [TestMethod]
    public async Task UnknownOption_FarFromEveryOption_ListsTheValidOnes_WithoutAGuess()
    {
        var(code, stdout, stderr) = await Run("report", "missing.xml", "--verbose");
        AssertTwoLineError(code, stdout, stderr, "Unknown option '--verbose' for 'report'.", ReportOptions);
    }

    [TestMethod]
    public async Task SingleDashSpelling_IsCorrectedToTheDoubleDashOption()
    {
        var(code, stdout, stderr) = await Run("report", "missing.xml", "-format", "json");
        AssertTwoLineError(code, stdout, stderr, "Unknown option '-format' for 'report'. Did you mean '--format'?", ReportOptions);
    }

    [TestMethod]
    public async Task UnknownOptionWrittenWithEquals_IsCorrectedByItsName_NotRewrittenAsIs()
    {
        // '--fromat json' would fail again: the advice is the option the user meant.
        var(code, stdout, stderr) = await Run("report", "missing.xml", "--fromat=json");
        AssertTwoLineError(code, stdout, stderr, "Unknown option '--fromat=json' for 'report'. Did you mean '--format'?", ReportOptions);
    }

    [TestMethod]
    public async Task HelpFlagInAnotherCase_IsCorrectedToHelp()
    {
        var(code, stdout, stderr) = await Run("report", "missing.xml", "--HELP");
        AssertTwoLineError(code, stdout, stderr, "Unknown option '--HELP' for 'report'. Did you mean '--help'?", ReportOptions);
    }

    [TestMethod]
    public async Task Test_MisspelledOption_GetsTheCorrection_AndTheSeparatorAdvice()
    {
        var(code, stdout, stderr) = await Run("test", "--min-lin", "80");
        AssertTwoLineError(code, stdout, stderr, "Unknown option '--min-lin' for 'test'. Did you mean '--min-line'? Pass dotnet test arguments after '--'.", "valid options for 'test': --min-line, --min-branch, --format, --top, --no-color, --exclude-generated, --exclude-test-code, " + "--exclude-external, --keep, --probe-sources, --source-root, --github-summary, --badge-dir, --help");
    }

    [TestMethod]
    public async Task PathSlotFlag_KeepsItsAdvice_AndListsTheValidOptions()
    {
        var(code, stdout, stderr) = await Run("report", "a.xml", "--file", "b.xml");
        AssertTwoLineError(code, stdout, stderr, "Unknown option '--file' for 'report'. Pass the path as a positional argument.", ReportOptions);
    }

    [TestMethod]
    [DataRow("check")]
    [DataRow("snapshot")]
    public async Task NoColor_IsAcceptedEvenWhereThereIsNoColour(string command)
    {
        // check prints verdict lines and snapshot JSON: nothing to turn off, but a script that
        // passes --no-color to every command must not get a usage error.
        var report = Path.Combine(Path.GetTempPath(), $"dotcov-nocolor-{Guid.NewGuid():N}.xml");
        await File.WriteAllTextAsync(report, """<coverage><packages><package name="p"><classes><class name="A" filename="src/A.cs"><lines><line number="1" hits="1"/></lines></class></classes></package></packages></coverage>""", cancellationToken: testContext.CancellationToken);
        try
        {
            string[] args = command is "check" ? [command, report, "--no-color", "--min-line", "40"] : [command, report, "--no-color"];
            var(code, stdout, stderr) = await Run(args);
            Assert.AreEqual(0, code, message: stderr);
            Assert.DoesNotContain("\u001b[", stdout + stderr);
        }
        finally
        {
            File.Delete(report);
        }
    }
}
