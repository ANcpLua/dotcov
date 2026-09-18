using DotCov.Tests.Infrastructure;
using DotCov.Tool;

namespace DotCov.Tests;

/// <summary>
/// Pins --github-summary behavior: the badge must derive from the same GateResult as the exit
/// code (a branch-gate failure once exited 1 while the summary showed ✅), the summary is
/// written on pass AND fail, and a bad GITHUB_STEP_SUMMARY path degrades to a warning instead
/// of aborting the command. Env-var dependent, so serialized under <see cref="ProcessState.Environment"/>.
/// </summary>
[NotInParallel(ProcessState.Environment)]
public sealed class CliGitHubSummaryTests : IDisposable
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
    private string BranchHalf() => _ws.Write("branch.cobertura.xml", Cobertura.NewDoc()
        .AddClass("src/B.cs", c => c.Line(1, hits: 1).Branch(2, "50% (1/2)")));

    private string HalfCovered() => _ws.Write("half.cobertura.xml", Cobertura.NewDoc()
        .AddClass("src/A.cs", c => c.Line(1, hits: 1).Line(2, hits: 0)));

    [Test]
    public async Task Check_BranchGateFailure_SummaryShowsFailBadge()
    {
        // The false-green regression: exit 1 for a branch-gate failure must not pair with a ✅
        // badge computed from a line-only re-evaluation.
        using var env = new EnvScope(("GITHUB_STEP_SUMMARY", SummaryPath));

        var (code, _, _) = await Run(
            "check", BranchHalf(), "--min-line", "50", "--min-branch", "90", "--github-summary");

        await Assert.That(code).IsEqualTo(1);
        var summary = await File.ReadAllTextAsync(SummaryPath);
        await Assert.That(summary).Contains("## Coverage Report ❌");
        // Verdict line comes from MarkdownFormatter.Format(report, gate) — backticked and
        // built from the same GateResult as the exit code.
        await Assert.That(summary).Contains("`FAIL:");
        await Assert.That(summary).Contains("branch coverage below threshold");
    }

    [Test]
    public async Task Check_PassingGate_StillWritesSummary()
    {
        // Green builds keep their coverage summary — previously the pass path returned before
        // the --github-summary handling and wrote nothing.
        using var env = new EnvScope(("GITHUB_STEP_SUMMARY", SummaryPath));

        var (code, _, _) = await Run("check", HalfCovered(), "--min-line", "40", "--github-summary");

        await Assert.That(code).IsEqualTo(0);
        var summary = await File.ReadAllTextAsync(SummaryPath);
        await Assert.That(summary).Contains("## Coverage Report ✅");
        await Assert.That(summary).Contains("`PASS:");
    }

    [Test]
    public async Task Check_NoData_SummaryShowsWarningBadge()
    {
        using var env = new EnvScope(("GITHUB_STEP_SUMMARY", SummaryPath));
        var empty = _ws.CreateDirectory("empty");

        var (code, _, _) = await Run("check", empty, "--min-line", "80", "--github-summary");

        await Assert.That(code).IsEqualTo(1);
        await Assert.That(await File.ReadAllTextAsync(SummaryPath)).Contains("## Coverage Report ⚠️");
    }

    [Test]
    public async Task Check_FailingRate_SummaryFloorsBodyToMatchVerdict()
    {
        // 1999/2500 = 79.96% under min-line 80: the verdict line floors to 79.9%, and the
        // markdown body (headline AND the offender file's table row) must floor with it —
        // never F1-round up to a self-contradictory 80.0% three lines below a FAIL.
        using var env = new EnvScope(("GITHUB_STEP_SUMMARY", SummaryPath));
        var path = _ws.Write("nearly80.cobertura.xml", CoberturaSamples.JustBelowEightyPercent());

        var (code, _, _) = await Run("check", path, "--min-line", "80", "--github-summary");

        await Assert.That(code).IsEqualTo(1);
        var summary = await File.ReadAllTextAsync(SummaryPath);
        await Assert.That(summary).Contains("## Coverage Report ❌");
        await Assert.That(summary).Contains("FAIL");
        await Assert.That(summary).Contains("**Line coverage:** 79.9% (1999/2500)");
        await Assert.That(summary).Contains("| `src/F.cs` | 1999/2500 | 79.9% |");
        await Assert.That(summary).Contains("**Threshold:** line 80%, branch 0%");
        await Assert.That(summary).DoesNotContain("80.0%");
    }

    [Test]
    public async Task Crap_FailingGate_SummaryShowsFailBadgeAndVerdict()
    {
        // Same no-false-green contract as check: the badge and the backticked verdict derive
        // from the same CrapGateResult as the exit code, written on fail too.
        using var env = new EnvScope(("GITHUB_STEP_SUMMARY", SummaryPath));
        var path = _ws.Write("crap.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/R.cs", "MyApp.R", c => c.Method("Risky", "()", "3", m => m.Line(1, hits: 0))));

        var (code, _, _) = await Run("crap", path, "--github-summary");

        await Assert.That(code).IsEqualTo(1);
        var summary = await File.ReadAllTextAsync(SummaryPath);
        await Assert.That(summary).Contains("## CRAP Report ❌");
        await Assert.That(summary).Contains("`FAIL: worst CRAP 12.0 (max 6)");
    }

    [Test]
    public async Task Crap_PassingGate_StillWritesSummary()
    {
        using var env = new EnvScope(("GITHUB_STEP_SUMMARY", SummaryPath));
        var path = _ws.Write("crap-pass.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/S.cs", "MyApp.S", c => c.Method("Safe", "()", "1", m => m.Line(1, hits: 1))));

        var (code, _, _) = await Run("crap", path, "--github-summary");

        await Assert.That(code).IsEqualTo(0);
        var summary = await File.ReadAllTextAsync(SummaryPath);
        await Assert.That(summary).Contains("## CRAP Report ✅");
        await Assert.That(summary).Contains("`PASS:");
    }

    [Test]
    public async Task Report_WritesSummary()
    {
        using var env = new EnvScope(("GITHUB_STEP_SUMMARY", SummaryPath));

        var (code, _, _) = await Run("report", HalfCovered(), "--github-summary");

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(await File.ReadAllTextAsync(SummaryPath)).Contains("## Coverage Report");
    }

    [Test]
    public async Task InvalidSummaryPath_WarnsAndPreservesExitCode()
    {
        using var env = new EnvScope(("GITHUB_STEP_SUMMARY", "/nonexistent-dir-dotcov-tests/sum.md"));

        var (reportCode, _, reportErr) = await Run("report", HalfCovered(), "--github-summary");
        await Assert.That(reportCode).IsEqualTo(0);
        await Assert.That(reportErr).Contains("warning: could not write GITHUB_STEP_SUMMARY");

        // The check failure path must keep its clean exit 1, not abort before returning it.
        var (checkCode, _, checkErr) = await Run(
            "check", HalfCovered(), "--min-line", "90", "--github-summary");
        await Assert.That(checkCode).IsEqualTo(1);
        await Assert.That(checkErr).Contains("FAIL");
        await Assert.That(checkErr).Contains("warning: could not write GITHUB_STEP_SUMMARY");
        await Assert.That(checkErr).DoesNotContain("Unhandled exception");
    }

    [Test]
    public async Task EnvVarUnset_FlagIsNoOp()
    {
        using var env = EnvScope.Clear("GITHUB_STEP_SUMMARY");

        var (code, _, _) = await Run("report", HalfCovered(), "--github-summary");

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(File.Exists(SummaryPath)).IsFalse();
    }
}
