using DotCov.Tool;

namespace DotCov.Tests;

/// <summary>
/// Mistakes that tell you the fix: an unknown option names the command, suggests the option
/// within two edits, and lists every option the command takes, on two stderr lines with exit 2
/// and nothing on stdout. Each is rejected before any report is read or test is run.
/// </summary>
public sealed class CliMistakeTests
{
    private static async Task<(int Code, string StdOut, string StdErr)> Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await DotCovCli.RunAsync(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private const string ReportOptions =
        "valid options for 'report': --format, --top, --threshold, --no-color, --exclude-generated, --exclude-test-code, " +
        "--keep, --probe-sources, --source-root, --pattern, --max-chars, --upload, --github-summary, --help";

    private static async Task AssertTwoLineError(int code, string stdout, string stderr, string first, string second)
    {
        await Assert.That(code).IsEqualTo(DotCovCli.UsageExitCode);
        await Assert.That(stdout).IsEmpty();
        await Assert.That(stderr).IsEqualTo($"error: {first}{Environment.NewLine}{second}{Environment.NewLine}");
    }

    [Test]
    [Arguments("report missing.xml --fromat json", "--fromat", "--format")]
    [Arguments("check missing.xml --min-lin 80", "--min-lin", "--min-line")]
    [Arguments("crap missing.xml --max-crp 6", "--max-crp", "--max-crap")]
    [Arguments("diff a.xml b.xml --patern x.xml", "--patern", "--pattern")]
    [Arguments("snapshot missing.xml --comit abc", "--comit", "--commit")]
    [Arguments("report missing.xml --no-colour", "--no-colour", "--no-color")]
    [Arguments("check missing.xml --min-line 80 --badgedir out", "--badgedir", "--badge-dir")]
    [Arguments("report missing.xml --EXCLUDE-GENERATD", "--EXCLUDE-GENERATD", "--exclude-generated")]
    public async Task UnknownOption_WithinTwoEdits_IsNamed_AndEveryValidOptionListed(string invocation, string typed, string meant)
    {
        var args = invocation.Split(' ');

        var (code, stdout, stderr) = await Run(args);

        var lines = stderr.Split(Environment.NewLine);
        await Assert.That(code).IsEqualTo(2);
        await Assert.That(stdout).IsEmpty();
        await Assert.That(lines).Count().IsEqualTo(3); // two lines and the final newline
        await Assert.That(lines[0]).IsEqualTo($"error: Unknown option '{typed}' for '{args[0]}'. Did you mean '{meant}'?");
        var prefix = $"valid options for '{args[0]}': ";
        await Assert.That(lines[1]).StartsWith(prefix);
        await Assert.That(lines[1][prefix.Length..].Split(", ")).Contains(meant);
    }

    [Test]
    public async Task UnknownOption_FarFromEveryOption_ListsTheValidOnes_WithoutAGuess()
    {
        var (code, stdout, stderr) = await Run("report", "missing.xml", "--verbose");

        await AssertTwoLineError(code, stdout, stderr, "Unknown option '--verbose' for 'report'.", ReportOptions);
    }

    [Test]
    public async Task SingleDashSpelling_IsCorrectedToTheDoubleDashOption()
    {
        var (code, stdout, stderr) = await Run("report", "missing.xml", "-format", "json");

        await AssertTwoLineError(code, stdout, stderr,
            "Unknown option '-format' for 'report'. Did you mean '--format'?", ReportOptions);
    }

    [Test]
    public async Task UnknownOptionWrittenWithEquals_IsCorrectedByItsName_NotRewrittenAsIs()
    {
        // '--fromat json' would fail again: the advice is the option the user meant.
        var (code, stdout, stderr) = await Run("report", "missing.xml", "--fromat=json");

        await AssertTwoLineError(code, stdout, stderr,
            "Unknown option '--fromat=json' for 'report'. Did you mean '--format'?", ReportOptions);
    }

    [Test]
    public async Task HelpFlagInAnotherCase_IsCorrectedToHelp()
    {
        var (code, stdout, stderr) = await Run("report", "missing.xml", "--HELP");

        await AssertTwoLineError(code, stdout, stderr,
            "Unknown option '--HELP' for 'report'. Did you mean '--help'?", ReportOptions);
    }

    [Test]
    public async Task Test_MisspelledOption_GetsTheCorrection_AndTheSeparatorAdvice()
    {
        var (code, stdout, stderr) = await Run("test", "--min-lin", "80");

        await AssertTwoLineError(code, stdout, stderr,
            "Unknown option '--min-lin' for 'test'. Did you mean '--min-line'? Pass dotnet test arguments after '--'.",
            "valid options for 'test': --min-line, --min-branch, --format, --top, --no-color, --exclude-generated, --exclude-test-code, " +
            "--keep, --probe-sources, --source-root, --github-summary, --badge-dir, --help");
    }

    [Test]
    public async Task PathSlotFlag_KeepsItsAdvice_AndListsTheValidOptions()
    {
        var (code, stdout, stderr) = await Run("report", "a.xml", "--file", "b.xml");

        await AssertTwoLineError(code, stdout, stderr,
            "Unknown option '--file' for 'report'. Pass the path as a positional argument.", ReportOptions);
    }

    [Test]
    [Arguments("check")]
    [Arguments("snapshot")]
    public async Task NoColor_IsAnOptionOnlyOfCommandsThatColour(string command)
    {
        // check prints verdict lines and snapshot JSON: neither has colour to turn off.
        var (code, stdout, stderr) = await Run(command, "missing.xml", "--no-color");

        await Assert.That(code).IsEqualTo(2);
        await Assert.That(stdout).IsEmpty();
        await Assert.That(stderr).StartsWith($"error: Unknown option '--no-color' for '{command}'.{Environment.NewLine}");
    }
}
