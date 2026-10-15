using DotCov.Tests.Infrastructure;
using DotCov.Tool;

namespace DotCov.Tests;
/// <summary>
/// File identity through the real dotcov process. The report declares two RELATIVE roots,
/// <c>src</c> and <c>tests</c>, so resolution against the disk would depend on the working
/// directory: by default it never does, and check, crap and test refuse a verdict until
/// <c>--probe-sources</c> or <c>--source-root</c> resolves the files. The Fallout parameters
/// are pinned in <see cref = "FalloutBuildTests"/>.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class FileIdentityCliTests : IDisposable
{
    private readonly TestContext _testContext;
    private const string Ambiguous = "NODATA: line 50.0% (min 40%), branch n/a (min 0%) - 1 file has ambiguous identity across 2 source roots; " + "re-run with --probe-sources on the machine that holds the sources or supply --source-root";
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov identity-");
    private readonly string _report;
    public FileIdentityCliTests(TestContext testContext)
    {
        _testContext = testContext;
        _report = _ws.Write("report/coverage.cobertura.xml", Cobertura.NewDoc().WithSource("src").WithSource("tests").AddClass("test_app.py", "test_app", c => c.Method("test_it", "()", "2", m => m.Line(1, 1).Line(2, 0)).Line(1, 1).Line(2, 0)));
    }

    public void Dispose() => _ws.Dispose();
    private Task<ChildRun> Dotcov(CancellationToken cancellationToken, string workingDirectory, params string[] args) => DotcovProcess.RunAsync(cancellationToken, workingDirectory, new Dictionary<string, string?>(), args);
    /// <summary>A machine that holds the sources: test_app.py exists under the SECOND root.</summary>
    private string SourceCheckout()
    {
        _ws.Write("checkout/tests/test_app.py", "");
        _ws.CreateDirectory("checkout/src");
        return _ws.PathOf("checkout");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Report_DefaultMode_GivesTheSameKeysAndWarnings_InEveryWorkingDirectory()
    {
        var cancellationToken = _testContext.CancellationToken;
        var withSources = await Dotcov(cancellationToken, SourceCheckout(), "report", _report, "--format", "json");
        var elsewhere = await Dotcov(cancellationToken, _ws.CreateDirectory("elsewhere"), "report", _report, "--format", "json");
        Assert.AreEqual(0, withSources.ExitCode, message: withSources.Output);
        Assert.AreEqual(elsewhere.StdOut, withSources.StdOut);
        Assert.AreEqual(elsewhere.StdErr, withSources.StdErr);
        Assert.Contains("\"path\": \"src/test_app.py\"", withSources.StdOut);
        Assert.Contains("\"kind\": \"FileIdentityAmbiguous\"", withSources.StdOut);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Report_Table_PrintsNormally_WithTheWarning()
    {
        var cancellationToken = _testContext.CancellationToken;
        var run = await Dotcov(cancellationToken, _ws.Root, "report", _report);
        Assert.AreEqual(0, run.ExitCode);
        Assert.Contains("src/test_app.py", run.StdOut);
        Assert.StartsWith("warning: src/test_app.py: relative filename 'test_app.py' could belong to any of 2 <source> roots ('src', 'tests')", run.StdErr);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ProbeSources_ReproducesTwoPointZero_OnTheMachineThatHoldsTheSources()
    {
        var cancellationToken = _testContext.CancellationToken;
        var run = await Dotcov(cancellationToken, SourceCheckout(), "report", _report, "--format", "json", "--probe-sources");
        Assert.AreEqual(0, run.ExitCode);
        Assert.Contains("\"path\": \"tests/test_app.py\"", run.StdOut);
        Assert.DoesNotContain("FileIdentityAmbiguous", run.StdOut);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Check_DefaultMode_IsNoData_WithTheRemedy()
    {
        var cancellationToken = _testContext.CancellationToken;
        var run = await Dotcov(cancellationToken, SourceCheckout(), "check", _report, "--min-line", "40");
        Assert.AreEqual(DotCovCli.NoDataExitCode, run.ExitCode);
        Assert.IsEmpty(run.StdOut);
        Assert.StartsWith(Ambiguous, run.StdErr);
    }

    [TestMethod]
    [DataRow("--probe-sources")]
    [DataRow("--source-root tests")]
    [DataRow("--source-root ./tests/")]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Check_ResolvedIdentity_IsAMeasuredVerdict(string flags)
    {
        var cancellationToken = _testContext.CancellationToken;
        var identity = flags.Split(' ');
        var run = await Dotcov(cancellationToken, SourceCheckout(), ["check", _report, "--min-line", "40", ..identity]);
        Assert.AreEqual(0, run.ExitCode, message: run.Output);
        Assert.IsEmpty(run.StdOut);
        // The verdict alone: a resolved identity leaves no warning behind it.
        Assert.StartsWith("PASS: line 50.0% (min 40%)", run.StdErr);
        Assert.ContainsSingle(run.StdErr.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task SourceRoot_TheReportDoesNotDeclare_IsAUsageError()
    {
        var cancellationToken = _testContext.CancellationToken;
        var run = await Dotcov(cancellationToken, _ws.Root, "check", _report, "--min-line", "40", "--source-root", "lib");
        Assert.AreEqual(DotCovCli.UsageExitCode, run.ExitCode);
        Assert.IsEmpty(run.StdOut);
        Assert.AreEqual($"error: source root 'lib' is not declared by {_report} (declared: 'src', 'tests').{Environment.NewLine}", run.StdErr);
    }

    [TestMethod]
    [DataRow("report")]
    [DataRow("crap")]
    [DataRow("snapshot")]
    [DataRow("diff")]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task BothModesAtOnce_OrABlankRoot_IsAUsageError_OnEveryReportReadingCommand(string command)
    {
        var cancellationToken = _testContext.CancellationToken;
        string[] paths = command is "diff" ? [_report, _report] : [_report];
        var both = await Dotcov(cancellationToken, _ws.Root, [command, ..paths, "--probe-sources", "--source-root", "src"]);
        var blank = await Dotcov(cancellationToken, _ws.Root, [command, ..paths, "--source-root", " "]);
        Assert.AreEqual(DotCovCli.UsageExitCode, both.ExitCode);
        Assert.AreEqual($"error: --probe-sources and --source-root are alternatives: pass one of them.{Environment.NewLine}", both.StdErr);
        Assert.AreEqual(DotCovCli.UsageExitCode, blank.ExitCode);
        Assert.StartsWith("error: Invalid --source-root value: ' '", blank.StdErr);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Crap_DefaultMode_IsNoData_AndAnExplicitRootMeasuresIt()
    {
        var cancellationToken = _testContext.CancellationToken;
        var ambiguous = await Dotcov(cancellationToken, _ws.Root, "crap", _report);
        var resolved = await Dotcov(cancellationToken, _ws.Root, "crap", _report, "--source-root", "tests");
        Assert.AreEqual(DotCovCli.NoDataExitCode, ambiguous.ExitCode);
        Assert.StartsWith("NODATA: worst CRAP 2.5 (max 30) - 1 file has ambiguous identity across 2 source roots;", ambiguous.StdErr);
        Assert.AreEqual(0, resolved.ExitCode, message: resolved.Output);
        Assert.StartsWith("PASS: worst CRAP 2.5 (max 30)", resolved.StdErr);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Test_DefaultMode_IsNoData_AndAnExplicitRootMeasuresIt()
    {
        var cancellationToken = _testContext.CancellationToken;
        var dotnet = FakeDotnet.Write(_ws, _report);
        var environment = new Dictionary<string, string?>
        {
            ["DOTNET_HOST_PATH"] = dotnet
        };
        var ambiguous = await DotcovProcess.RunAsync(cancellationToken, _ws.Root, environment, "test", "--min-line", "40");
        var resolved = await DotcovProcess.RunAsync(cancellationToken, _ws.Root, environment, "test", "--min-line", "40", "--source-root", "tests");
        Assert.AreEqual(DotCovCli.NoDataExitCode, ambiguous.ExitCode, message: ambiguous.Output);
        Assert.StartsWith(Ambiguous, ambiguous.StdErr);
        Assert.AreEqual(0, resolved.ExitCode, message: resolved.Output);
        Assert.StartsWith("PASS: line 50.0% (min 40%)", resolved.StdErr);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Test_BranchThresholdAlone_AfterARun_LeavesLinesUnchecked()
    {
        var cancellationToken = _testContext.CancellationToken;
        // The post-run half of the threshold contract, which a real test run would take minutes to show.
        var report = _ws.Write("branches.cobertura.xml", Cobertura.NewDoc().AddClass("src/M.cs", c => c.Line(1, hits: 0).Branch(2, "100% (2/2)")));
        var run = await DotcovProcess.RunAsync(cancellationToken, _ws.Root, new Dictionary<string, string?> { ["DOTNET_HOST_PATH"] = FakeDotnet.Write(_ws, report) }, "test", "--min-branch", "90");
        Assert.AreEqual(0, run.ExitCode, message: run.Output);
        Assert.StartsWith("PASS: line 50.0% (min 0%), branch 100.0% (min 90%)", run.StdErr);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Diff_AndSnapshot_ResolveWithTheSameFlags()
    {
        var cancellationToken = _testContext.CancellationToken;
        var diff = await Dotcov(cancellationToken, _ws.Root, "diff", _report, _report, "--format", "json", "--source-root", "tests");
        var snapshot = await Dotcov(cancellationToken, _ws.Root, "snapshot", _report, "--commit", "c", "--branch", "b", "--project", "p", "--source-root", "tests");
        Assert.AreEqual(0, diff.ExitCode, message: diff.Output);
        Assert.Contains("\"path\": \"tests/test_app.py\"", diff.StdOut);
        Assert.AreEqual(0, snapshot.ExitCode, message: snapshot.Output);
        Assert.Contains("\"path\": \"tests/test_app.py\"", snapshot.StdOut);
    }
}
