using System.Text.Json;
using DotCov.Tests.Infrastructure;
using DotCov.Tool;

namespace DotCov.Tests;
/// <summary>
/// Pins --badge-dir on the gating commands: both files are written for every outcome from the
/// gate that set the exit code, a badge that cannot be written turns a pass into exit 5 while a
/// non-passing gate keeps its code, and a bad value fails before any report is read or test runs.
/// </summary>
[TestClass]
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
    private string HalfCovered() => _ws.Write("half.cobertura.xml", Cobertura.NewDoc().AddClass("src/A.cs", c => c.Line(1, hits: 1).Line(2, hits: 0)));
    /// <summary>100% line coverage, 50% branch coverage (1/2).</summary>
    private string BranchHalf() => _ws.Write("branch.cobertura.xml", Cobertura.NewDoc().AddClass("src/B.cs", c => c.Line(1, hits: 1).Branch(2, "50% (1/2)")));
    [TestMethod]
    [DataRow("40", 0, "50.0%", "brightgreen")]
    [DataRow("80", 1, "50.0%", "red")]
    [DataRow("0", 4, "disabled", "yellow")]
    public async Task Check_WritesTheGateBadge_IntoANewDirectory(string minLine, int expectedCode, string message, string color)
    {
        var directory = _ws.PathOf("out/badge");
        var(code, stdout, stderr) = await Run("check", HalfCovered(), "--min-line", minLine, "--badge-dir", directory);
        Assert.AreEqual(expectedCode, code, message: stderr);
        await AssertBadge(directory, message, color, VerdictLine(stdout, stderr));
    }

    [TestMethod]
    public async Task Check_NoData_WritesANoDataBadge()
    {
        var directory = _ws.PathOf("badge");
        var(code, stdout, stderr) = await Run("check", _ws.CreateDirectory("empty"), "--min-line", "80", "--badge-dir", directory);
        Assert.AreEqual(3, code);
        await AssertBadge(directory, "no data", "lightgrey", VerdictLine(stdout, stderr));
    }

    [TestMethod]
    public async Task Check_BranchBelowItsMinimum_TurnsTheBadgeRed_AtFullLineCoverage()
    {
        // The case a line-rate badge gets wrong: 100.0% lines, the gate failed on branches.
        var directory = _ws.PathOf("badge");
        var(code, stdout, stderr) = await Run("check", BranchHalf(), "--min-line", "100", "--min-branch", "90", "--badge-dir", directory);
        Assert.AreEqual(1, code);
        await AssertBadge(directory, "100.0% / 50.0%", "red", VerdictLine(stdout, stderr));
    }

    [TestMethod]
    public async Task Check_ExistingBadge_IsReplaced()
    {
        var directory = _ws.PathOf("badge");
        await Run("check", HalfCovered(), "--min-line", "40", "--badge-dir", directory);
        var(code, stdout, stderr) = await Run("check", HalfCovered(), "--min-line", "80", "--badge-dir", directory);
        Assert.AreEqual(1, code);
        await AssertBadge(directory, "50.0%", "red", VerdictLine(stdout, stderr));
    }

    [TestMethod]
    public async Task Check_PassingGate_BadgeUnwritable_Exits5_ErrorAfterTheVerdict()
    {
        var blocked = _ws.Write("blocked", "a file where the directory should go");
        var(code, stdout, stderr) = await Run("check", HalfCovered(), "--min-line", "40", "--badge-dir", blocked);
        Assert.AreEqual(5, code);
        Assert.IsEmpty(stdout);
        var lines = stderr.Split(Environment.NewLine);
        Assert.StartsWith("PASS: line 50.0% (min 40%)", lines[0]);
        Assert.StartsWith($"error: could not write the badge to '{blocked}': ", lines[1]);
    }

    [TestMethod]
    [DataRow("80", 1, "FAIL:")]
    [DataRow("0", 4, "DISABLED:")]
    public async Task Check_NonPassingGate_BadgeUnwritable_KeepsItsCode(string minLine, int expectedCode, string verdict)
    {
        var blocked = _ws.Write("blocked", "a file where the directory should go");
        var(code, _, stderr) = await Run("check", HalfCovered(), "--min-line", minLine, "--badge-dir", blocked);
        Assert.AreEqual(expectedCode, code);
        Assert.StartsWith(verdict, stderr);
        Assert.Contains($"error: could not write the badge to '{blocked}': ", stderr);
    }

    [TestMethod]
    public async Task Check_EmptyBadgeDir_FailsBeforeReadingTheReport()
    {
        var missing = _ws.PathOf("missing.xml");
        var(code, stdout, stderr) = await Run("check", missing, "--min-line", "80", "--badge-dir", "");
        Assert.AreEqual(2, code);
        Assert.IsEmpty(stdout);
        Assert.AreEqual($"error: Invalid --badge-dir value: '' (expected a directory).{Environment.NewLine}", stderr);
    }

    [TestMethod]
    public async Task Test_EmptyBadgeDir_FailsBeforeAnyTestRuns()
    {
        var(code, stdout, stderr) = await Run("test", "--min-line", "80", "--badge-dir", "");
        Assert.AreEqual(2, code);
        // No echoed "> dotnet test" line: nothing was started.
        Assert.IsEmpty(stdout);
        Assert.StartsWith("error: Invalid --badge-dir value: ''", stderr);
    }

    [TestMethod]
    [DataRow("report")]
    [DataRow("crap")]
    [DataRow("snapshot")]
    public async Task CommandsWithoutAGate_RejectBadgeDir(string command)
    {
        var directory = _ws.PathOf("badge");
        var(code, stdout, stderr) = await Run(command, HalfCovered(), "--badge-dir", directory);
        Assert.AreEqual(2, code);
        Assert.IsEmpty(stdout);
        Assert.StartsWith($"error: Unknown option '--badge-dir' for '{command}'.", stderr);
        Assert.IsFalse(Directory.Exists(directory));
    }

    /// <summary>The verdict line: stdout carries a pass, stderr leads with any other outcome.</summary>
    /// <summary>The verdict check printed: the first stderr line on every outcome, with nothing on stdout.</summary>
    private static string VerdictLine(string stdout, string stderr) => stdout.Length is 0 ? stderr.Split(Environment.NewLine)[0] : $"unexpected stdout: {stdout}";
    /// <summary>Both files, from the gate whose verdict the command printed.</summary>
    private static async Task AssertBadge(string directory, string message, string color, string verdict)
    {
        var svg = await File.ReadAllTextAsync(Path.Combine(directory, "coverage-badge.svg"));
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "coverage-badge.json")));
        Assert.Contains($"<title>Coverage: {verdict}</title>", svg);
        Assert.Contains($">{message}</text>", svg);
        Assert.AreEqual(message, json.RootElement.GetProperty("message").GetString());
        Assert.AreEqual(color, json.RootElement.GetProperty("color").GetString());
    }
}
