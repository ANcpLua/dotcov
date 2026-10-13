using System.Text.Json;
using DotCov.Tests.Infrastructure;
using DotCov.Tool;

namespace DotCov.Tests;

/// <summary>
/// Pins --badge-dir on the gating commands: both files are written for every outcome from the
/// gate that set the exit code, a badge that cannot be written turns a pass into exit 5 while a
/// non-passing gate keeps its code, and a bad value fails before any report is read or test runs.
/// </summary>
public sealed class CliBadgeTests : IDisposable
{
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-cli-badge-");

    public void Dispose() => _ws.Dispose();

    private static async Task<(int Code, string StdOut, string StdErr)> Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await DotCovCli.RunAsync(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    /// <summary>50% line coverage, no branch data.</summary>
    private string HalfCovered() => _ws.Write("half.cobertura.xml", Cobertura.NewDoc()
        .AddClass("src/A.cs", c => c.Line(1, hits: 1).Line(2, hits: 0)));

    /// <summary>100% line coverage, 50% branch coverage (1/2).</summary>
    private string BranchHalf() => _ws.Write("branch.cobertura.xml", Cobertura.NewDoc()
        .AddClass("src/B.cs", c => c.Line(1, hits: 1).Branch(2, "50% (1/2)")));

    [Test]
    [Arguments("40", 0, "50.0%", "brightgreen")]
    [Arguments("80", 1, "50.0%", "red")]
    [Arguments("0", 4, "disabled", "yellow")]
    public async Task Check_WritesTheGateBadge_IntoANewDirectory(string minLine, int expectedCode, string message, string color)
    {
        var directory = _ws.PathOf("out/badge");

        var (code, stdout, stderr) = await Run("check", HalfCovered(), "--min-line", minLine, "--badge-dir", directory);

        await Assert.That(code).IsEqualTo(expectedCode).Because(stderr);
        await AssertBadge(directory, message, color, VerdictLine(code, stdout, stderr));
    }

    [Test]
    public async Task Check_NoData_WritesANoDataBadge()
    {
        var directory = _ws.PathOf("badge");

        var (code, stdout, stderr) = await Run("check", _ws.CreateDirectory("empty"), "--min-line", "80", "--badge-dir", directory);

        await Assert.That(code).IsEqualTo(3);
        await AssertBadge(directory, "no data", "lightgrey", VerdictLine(code, stdout, stderr));
    }

    [Test]
    public async Task Check_BranchBelowItsMinimum_TurnsTheBadgeRed_AtFullLineCoverage()
    {
        // The case a line-rate badge gets wrong: 100.0% lines, the gate failed on branches.
        var directory = _ws.PathOf("badge");

        var (code, stdout, stderr) = await Run("check", BranchHalf(), "--min-line", "100", "--min-branch", "90", "--badge-dir", directory);

        await Assert.That(code).IsEqualTo(1);
        await AssertBadge(directory, "100.0% / 50.0%", "red", VerdictLine(code, stdout, stderr));
    }

    [Test]
    public async Task Check_ExistingBadge_IsReplaced()
    {
        var directory = _ws.PathOf("badge");
        await Run("check", HalfCovered(), "--min-line", "40", "--badge-dir", directory);

        var (code, stdout, stderr) = await Run("check", HalfCovered(), "--min-line", "80", "--badge-dir", directory);

        await Assert.That(code).IsEqualTo(1);
        await AssertBadge(directory, "50.0%", "red", VerdictLine(code, stdout, stderr));
    }

    [Test]
    public async Task Check_PassingGate_BadgeUnwritable_Exits5_ErrorFirst()
    {
        var blocked = _ws.Write("blocked", "a file where the directory should go");

        var (code, stdout, stderr) = await Run("check", HalfCovered(), "--min-line", "40", "--badge-dir", blocked);

        await Assert.That(code).IsEqualTo(5);
        await Assert.That(stdout).Contains("PASS: line 50.0%");
        await Assert.That(stderr).StartsWith($"error: could not write the badge to '{blocked}': ");
    }

    [Test]
    [Arguments("80", 1, "FAIL:")]
    [Arguments("0", 4, "DISABLED:")]
    public async Task Check_NonPassingGate_BadgeUnwritable_KeepsItsCode(string minLine, int expectedCode, string verdict)
    {
        var blocked = _ws.Write("blocked", "a file where the directory should go");

        var (code, _, stderr) = await Run("check", HalfCovered(), "--min-line", minLine, "--badge-dir", blocked);

        await Assert.That(code).IsEqualTo(expectedCode);
        await Assert.That(stderr).StartsWith(verdict);
        await Assert.That(stderr).Contains($"error: could not write the badge to '{blocked}': ");
    }

    [Test]
    public async Task Check_EmptyBadgeDir_FailsBeforeReadingTheReport()
    {
        var missing = _ws.PathOf("missing.xml");

        var (code, stdout, stderr) = await Run("check", missing, "--min-line", "80", "--badge-dir", "");

        await Assert.That(code).IsEqualTo(2);
        await Assert.That(stdout).IsEmpty();
        await Assert.That(stderr).IsEqualTo($"error: Invalid --badge-dir value: '' (expected a directory).{Environment.NewLine}");
    }

    [Test]
    public async Task Test_EmptyBadgeDir_FailsBeforeAnyTestRuns()
    {
        var (code, stdout, stderr) = await Run("test", "--min-line", "80", "--badge-dir", "");

        await Assert.That(code).IsEqualTo(2);
        // No echoed "> dotnet test" line: nothing was started.
        await Assert.That(stdout).IsEmpty();
        await Assert.That(stderr).StartsWith("error: Invalid --badge-dir value: ''");
    }

    [Test]
    [Arguments("report")]
    [Arguments("crap")]
    [Arguments("snapshot")]
    public async Task CommandsWithoutAGate_RejectBadgeDir(string command)
    {
        var directory = _ws.PathOf("badge");

        var (code, stdout, stderr) = await Run(command, HalfCovered(), "--badge-dir", directory);

        await Assert.That(code).IsEqualTo(2);
        await Assert.That(stdout).IsEmpty();
        await Assert.That(stderr).StartsWith($"error: Unknown option '--badge-dir' for '{command}'.");
        await Assert.That(Directory.Exists(directory)).IsFalse();
    }

    /// <summary>The verdict line: stdout carries a pass, stderr leads with any other outcome.</summary>
    private static string VerdictLine(int code, string stdout, string stderr) =>
        (code is 0 ? stdout : stderr).Split(Environment.NewLine)[0];

    /// <summary>Both files, from the gate whose verdict the command printed.</summary>
    private static async Task AssertBadge(string directory, string message, string color, string verdict)
    {
        var svg = await File.ReadAllTextAsync(Path.Combine(directory, "coverage-badge.svg"));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "coverage-badge.json")));

        await Assert.That(svg).Contains($"<title>Coverage: {verdict}</title>");
        await Assert.That(svg).Contains($">{message}</text>");
        await Assert.That(json.RootElement.GetProperty("message").GetString()).IsEqualTo(message);
        await Assert.That(json.RootElement.GetProperty("color").GetString()).IsEqualTo(color);
    }
}
