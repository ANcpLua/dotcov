using DotCov.Tests.Infrastructure;
using DotCov.Tool;
using Xunit;

namespace DotCov.Tests;

/// <summary>
/// Pins --github-summary behavior: the badge must derive from the same GateResult as the exit
/// code (a branch-gate failure once exited 1 while the summary showed ✅), the summary is
/// written on pass AND fail, and a bad GITHUB_STEP_SUMMARY path degrades to a warning instead
/// of aborting the command. Env-var dependent, so serialized via <see cref="EnvCollection"/>.
/// </summary>
[Collection(nameof(EnvCollection))]
public sealed class CliGitHubSummaryTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("dotcov-cli-summary-");

    public void Dispose() => _dir.Delete(recursive: true);

    private string SummaryPath => Path.Combine(_dir.FullName, "step-summary.md");

    private static async Task<(int Code, string StdOut, string StdErr)> Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await DotCovCli.RunAsync(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private string WriteFixture(string name, Cobertura doc)
    {
        var path = Path.Combine(_dir.FullName, name);
        File.WriteAllBytes(path, doc.ToBytes());
        return path;
    }

    /// <summary>100% line coverage, 50% branch coverage — passes any line gate, fails a 90% branch gate.</summary>
    private string BranchHalf() => WriteFixture("branch.cobertura.xml", Cobertura.NewDoc()
        .AddClass("src/B.cs", c => c.Line(1, hits: 1).Branch(2, "50% (1/2)")));

    private string HalfCovered() => WriteFixture("half.cobertura.xml", Cobertura.NewDoc()
        .AddClass("src/A.cs", c => c.Line(1, hits: 1).Line(2, hits: 0)));

    [Fact]
    public async Task Check_BranchGateFailure_SummaryShowsFailBadge()
    {
        // The false-green regression: exit 1 for a branch-gate failure must not pair with a ✅
        // badge computed from a line-only re-evaluation.
        using var env = new EnvScope(("GITHUB_STEP_SUMMARY", SummaryPath));

        var (code, _, _) = await Run(
            "check", BranchHalf(), "--min-line", "50", "--min-branch", "90", "--github-summary");

        Assert.Equal(1, code);
        var summary = File.ReadAllText(SummaryPath);
        Assert.Contains("## Coverage Report ❌", summary);
        Assert.Contains("FAIL", summary);
        Assert.Contains("branch coverage below threshold", summary);
    }

    [Fact]
    public async Task Check_PassingGate_StillWritesSummary()
    {
        // Green builds keep their coverage summary — previously the pass path returned before
        // the --github-summary handling and wrote nothing.
        using var env = new EnvScope(("GITHUB_STEP_SUMMARY", SummaryPath));

        var (code, _, _) = await Run("check", HalfCovered(), "--min-line", "40", "--github-summary");

        Assert.Equal(0, code);
        var summary = File.ReadAllText(SummaryPath);
        Assert.Contains("## Coverage Report ✅", summary);
        Assert.Contains("PASS", summary);
    }

    [Fact]
    public async Task Check_NoData_SummaryShowsWarningBadge()
    {
        using var env = new EnvScope(("GITHUB_STEP_SUMMARY", SummaryPath));
        var empty = Directory.CreateDirectory(Path.Combine(_dir.FullName, "empty")).FullName;

        var (code, _, _) = await Run("check", empty, "--min-line", "80", "--github-summary");

        Assert.Equal(1, code);
        Assert.Contains("## Coverage Report ⚠️", File.ReadAllText(SummaryPath));
    }

    [Fact]
    public async Task Check_FailingRate_SummaryFloorsBodyToMatchVerdict()
    {
        // 1999/2500 = 79.96% under min-line 80: the verdict line floors to 79.9%, and the
        // markdown body (headline AND the offender file's table row) must floor with it —
        // never F1-round up to a self-contradictory 80.0% three lines below a FAIL.
        using var env = new EnvScope(("GITHUB_STEP_SUMMARY", SummaryPath));
        var path = WriteFixture("nearly80.cobertura.xml", CliTests.NearlyEighty());

        var (code, _, _) = await Run("check", path, "--min-line", "80", "--github-summary");

        Assert.Equal(1, code);
        var summary = File.ReadAllText(SummaryPath);
        Assert.Contains("## Coverage Report ❌", summary);
        Assert.Contains("FAIL", summary);
        Assert.Contains("**Line coverage:** 79.9% (1999/2500)", summary);
        Assert.Contains("| `src/F.cs` | 1999/2500 | 79.9% |", summary);
        Assert.Contains("**Threshold:** line 80%, branch 0%", summary);
        Assert.DoesNotContain("80.0%", summary);
    }

    [Fact]
    public async Task Report_WritesSummary()
    {
        using var env = new EnvScope(("GITHUB_STEP_SUMMARY", SummaryPath));

        var (code, _, _) = await Run("report", HalfCovered(), "--github-summary");

        Assert.Equal(0, code);
        Assert.Contains("## Coverage Report", File.ReadAllText(SummaryPath));
    }

    [Fact]
    public async Task InvalidSummaryPath_WarnsAndPreservesExitCode()
    {
        using var env = new EnvScope(("GITHUB_STEP_SUMMARY", "/nonexistent-dir-dotcov-tests/sum.md"));

        var (reportCode, _, reportErr) = await Run("report", HalfCovered(), "--github-summary");
        Assert.Equal(0, reportCode);
        Assert.Contains("warning: could not write GITHUB_STEP_SUMMARY", reportErr);

        // The check failure path must keep its clean exit 1, not abort before returning it.
        var (checkCode, _, checkErr) = await Run(
            "check", HalfCovered(), "--min-line", "90", "--github-summary");
        Assert.Equal(1, checkCode);
        Assert.Contains("FAIL", checkErr);
        Assert.Contains("warning: could not write GITHUB_STEP_SUMMARY", checkErr);
        Assert.DoesNotContain("Unhandled exception", checkErr);
    }

    [Fact]
    public async Task EnvVarUnset_FlagIsNoOp()
    {
        using var env = EnvScope.Clear("GITHUB_STEP_SUMMARY");

        var (code, _, _) = await Run("report", HalfCovered(), "--github-summary");

        Assert.Equal(0, code);
        Assert.False(File.Exists(SummaryPath));
    }
}
