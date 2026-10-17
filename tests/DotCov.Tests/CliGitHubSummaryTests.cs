using DotCov.Tests.Infrastructure;
using DotCov.Tool;

namespace DotCov.Tests;
/// <summary>
/// Pins --github-summary behavior: the badge must derive from the same GateResult as the exit
/// code, the summary is written on pass AND fail, and a bad GITHUB_STEP_SUMMARY path degrades
/// to a warning instead of aborting the command. Env-var dependent, so serialized under
/// <see cref = "ProcessState.Environment"/>.
/// </summary>
[TestClass]
[ResourceLock(ProcessState.Environment)]
public sealed class CliGitHubSummaryTests(TestContext testContext) : IDisposable
{
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-cli-summary-");
    public void Dispose() => _ws.Dispose();
    private string SummaryPath => _ws.PathOf("step-summary.md");

    private static async Task<(int Code, string StdOut, string StdErr)> Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await DotCovCli.RunAsync(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    /// <summary>100% line coverage, 50% branch coverage — passes any line gate, fails a 90% branch gate.</summary>
    private string BranchHalf() => _ws.Write("branch.cobertura.xml", Cobertura.NewDoc().AddClass("src/B.cs", c => c.Line(1, hits: 1).Branch(2, "50% (1/2)")));
    private string HalfCovered() => _ws.Write("half.cobertura.xml", Cobertura.NewDoc().AddClass("src/A.cs", c => c.Line(1, hits: 1).Line(2, hits: 0)));
    [TestMethod]
    public async Task Check_BranchGateFailure_SummaryShowsFailBadge()
    {
        // Exit 1 for a branch-gate failure must not pair with a ✅ badge computed from a
        // line-only re-evaluation.
        using var env = new EnvScope(("GITHUB_STEP_SUMMARY", SummaryPath));
        var(code, _, _) = await Run("check", BranchHalf(), "--min-line", "50", "--min-branch", "90", "--github-summary");
        Assert.AreEqual(1, code);
        var summary = await File.ReadAllTextAsync(SummaryPath, cancellationToken: testContext.CancellationToken);
        Assert.Contains("## Coverage Report ❌", summary);
        // Verdict line comes from MarkdownFormatter.Format(report, gate) — backticked and
        // built from the same GateResult as the exit code.
        Assert.Contains("`FAIL:", summary);
        Assert.Contains("branch coverage below threshold", summary);
    }

    [TestMethod]
    public async Task Check_PassingGate_StillWritesSummary()
    {
        using var env = new EnvScope(("GITHUB_STEP_SUMMARY", SummaryPath));
        var(code, _, _) = await Run("check", HalfCovered(), "--min-line", "40", "--github-summary");
        Assert.AreEqual(0, code);
        var summary = await File.ReadAllTextAsync(SummaryPath, cancellationToken: testContext.CancellationToken);
        Assert.Contains("## Coverage Report ✅", summary);
        Assert.Contains("`PASS:", summary);
    }

    [TestMethod]
    public async Task Check_NoData_SummaryShowsWarningBadge()
    {
        using var env = new EnvScope(("GITHUB_STEP_SUMMARY", SummaryPath));
        var empty = _ws.CreateDirectory("empty");
        var(code, _, _) = await Run("check", empty, "--min-line", "80", "--github-summary");
        Assert.AreEqual(3, code);
        Assert.Contains("## Coverage Report ⚠️", await File.ReadAllTextAsync(SummaryPath, cancellationToken: testContext.CancellationToken));
    }

    [TestMethod]
    public async Task Check_FailingRate_SummaryFloorsBodyToMatchVerdict()
    {
        // 1999/2500 = 79.96% under min-line 80: the verdict line floors to 79.9%, and the
        // markdown body (headline AND the offender file's table row) must floor with it —
        // never F1-round up to a self-contradictory 80.0% three lines below a FAIL.
        using var env = new EnvScope(("GITHUB_STEP_SUMMARY", SummaryPath));
        var path = _ws.Write("nearly80.cobertura.xml", CoberturaSamples.JustBelowEightyPercent());
        var(code, _, _) = await Run("check", path, "--min-line", "80", "--github-summary");
        Assert.AreEqual(1, code);
        var summary = await File.ReadAllTextAsync(SummaryPath, cancellationToken: testContext.CancellationToken);
        Assert.Contains("## Coverage Report ❌", summary);
        Assert.Contains("FAIL", summary);
        Assert.Contains("**Line coverage:** 79.9% (1999/2500)", summary);
        Assert.Contains("| `src/F.cs` | 1999/2500 | 79.9% |", summary);
        Assert.Contains("**Threshold:** line 80%, branch 0%", summary);
        Assert.DoesNotContain("80.0%", summary);
    }

    [TestMethod]
    public async Task Crap_FailingGate_SummaryShowsFailBadgeAndVerdict()
    {
        // Same no-false-green contract as check: the badge and the backticked verdict derive
        // from the same CrapGateResult as the exit code, written on fail too.
        using var env = new EnvScope(("GITHUB_STEP_SUMMARY", SummaryPath));
        var path = _ws.Write("crap.cobertura.xml", Cobertura.NewDoc().AddClass("src/R.cs", "MyApp.R", c => c.Method("Risky", "()", "6", m => m.Line(1, hits: 0))));
        var(code, _, _) = await Run("crap", path, "--github-summary");
        Assert.AreEqual(1, code);
        var summary = await File.ReadAllTextAsync(SummaryPath, cancellationToken: testContext.CancellationToken);
        Assert.Contains("## CRAP Report ❌", summary);
        Assert.Contains("`FAIL: worst CRAP 42.0 (max 30)", summary);
    }

    [TestMethod]
    public async Task Crap_PassingGate_StillWritesSummary()
    {
        using var env = new EnvScope(("GITHUB_STEP_SUMMARY", SummaryPath));
        var path = _ws.Write("crap-pass.cobertura.xml", Cobertura.NewDoc().AddClass("src/S.cs", "MyApp.S", c => c.Method("Safe", "()", "1", m => m.Line(1, hits: 1))));
        var(code, _, _) = await Run("crap", path, "--github-summary");
        Assert.AreEqual(0, code);
        var summary = await File.ReadAllTextAsync(SummaryPath, cancellationToken: testContext.CancellationToken);
        Assert.Contains("## CRAP Report ✅", summary);
        Assert.Contains("`PASS:", summary);
    }

    [TestMethod]
    public async Task Report_WritesSummary()
    {
        using var env = new EnvScope(("GITHUB_STEP_SUMMARY", SummaryPath));
        var(code, _, _) = await Run("report", HalfCovered(), "--github-summary");
        Assert.AreEqual(0, code);
        Assert.Contains("## Coverage Report", await File.ReadAllTextAsync(SummaryPath, cancellationToken: testContext.CancellationToken));
    }

    [TestMethod]
    public async Task InvalidSummaryPath_WarnsAndPreservesExitCode()
    {
        using var env = new EnvScope(("GITHUB_STEP_SUMMARY", "/nonexistent-dir-dotcov-tests/sum.md"));
        var(reportCode, _, reportErr) = await Run("report", HalfCovered(), "--github-summary");
        Assert.AreEqual(0, reportCode);
        Assert.Contains("warning: could not write GITHUB_STEP_SUMMARY", reportErr);
        // The check failure path must keep its clean exit 1, not abort before returning it.
        var(checkCode, _, checkErr) = await Run("check", HalfCovered(), "--min-line", "90", "--github-summary");
        Assert.AreEqual(1, checkCode);
        // The summary warning follows the verdict, so the verdict stays the first stderr token.
        Assert.StartsWith("FAIL:", checkErr);
        Assert.Contains("warning: could not write GITHUB_STEP_SUMMARY", checkErr);
        Assert.DoesNotContain("Unhandled exception", checkErr);
    }

    [TestMethod]
    public async Task EnvVarUnset_FlagIsNoOp()
    {
        using var env = EnvScope.Clear("GITHUB_STEP_SUMMARY");
        var(code, _, _) = await Run("report", HalfCovered(), "--github-summary");
        Assert.AreEqual(0, code);
        Assert.IsFalse(File.Exists(SummaryPath));
    }
}
