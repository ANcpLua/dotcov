using DotCov.Tests.Infrastructure;
using DotCov.Tool;

namespace DotCov.Tests;

/// <summary>
/// File identity through the real dotcov process. The report declares two RELATIVE roots,
/// <c>src</c> and <c>tests</c>, so resolution against the disk would depend on the working
/// directory: by default it never does, and check, crap and test refuse a verdict until
/// <c>--probe-sources</c> or <c>--source-root</c> resolves the files. The Fallout parameters
/// are pinned in <see cref="FalloutBuildTests"/>.
/// </summary>
[Category("Integration")]
[Timeout(60_000)]
public sealed class FileIdentityCliTests : IDisposable
{
    private const string Ambiguous =
        "NODATA: line 50.0% (min 40%), branch n/a (min 0%) - 1 file has ambiguous identity across 2 source roots; " +
        "re-run with --probe-sources on the machine that holds the sources or supply --source-root";

    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov identity-");
    private readonly string _report;

    public FileIdentityCliTests() =>
        _report = _ws.Write("report/coverage.cobertura.xml", Cobertura.NewDoc()
            .WithSource("src").WithSource("tests")
            .AddClass("test_app.py", "test_app", c => c
                .Method("test_it", "()", "2", m => m.Line(1, 1).Line(2, 0))
                .Line(1, 1).Line(2, 0)));

    public void Dispose() => _ws.Dispose();

    private Task<ChildRun> Dotcov(CancellationToken cancellationToken, string workingDirectory, params string[] args) =>
        DotcovProcess.RunAsync(cancellationToken, workingDirectory, new Dictionary<string, string?>(), args);

    /// <summary>A machine that holds the sources: test_app.py exists under the SECOND root.</summary>
    private string SourceCheckout()
    {
        _ws.Write("checkout/tests/test_app.py", "");
        _ws.CreateDirectory("checkout/src");
        return _ws.PathOf("checkout");
    }

    [Test]
    public async Task Report_DefaultMode_GivesTheSameKeysAndWarnings_InEveryWorkingDirectory(CancellationToken cancellationToken)
    {
        var withSources = await Dotcov(cancellationToken, SourceCheckout(), "report", _report, "--format", "json");
        var elsewhere = await Dotcov(cancellationToken, _ws.CreateDirectory("elsewhere"), "report", _report, "--format", "json");

        await Assert.That(withSources.ExitCode).IsEqualTo(0).Because(withSources.Output);
        await Assert.That(withSources.StdOut).IsEqualTo(elsewhere.StdOut);
        await Assert.That(withSources.StdErr).IsEqualTo(elsewhere.StdErr);
        await Assert.That(withSources.StdOut).Contains("\"path\": \"src/test_app.py\"");
        await Assert.That(withSources.StdOut).Contains("\"kind\": \"FileIdentityAmbiguous\"");
    }

    [Test]
    public async Task Report_Table_PrintsNormally_WithTheWarning(CancellationToken cancellationToken)
    {
        var run = await Dotcov(cancellationToken, _ws.Root, "report", _report);

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(run.StdOut).Contains("src/test_app.py");
        await Assert.That(run.StdErr).StartsWith(
            "warning: src/test_app.py: relative filename 'test_app.py' could belong to any of 2 <source> roots ('src', 'tests')");
    }

    [Test]
    public async Task ProbeSources_ReproducesTwoPointZero_OnTheMachineThatHoldsTheSources(CancellationToken cancellationToken)
    {
        var run = await Dotcov(cancellationToken, SourceCheckout(), "report", _report, "--format", "json", "--probe-sources");

        await Assert.That(run.ExitCode).IsEqualTo(0);
        await Assert.That(run.StdOut).Contains("\"path\": \"tests/test_app.py\"");
        await Assert.That(run.StdOut).DoesNotContain("FileIdentityAmbiguous");
    }

    [Test]
    public async Task Check_DefaultMode_IsNoData_WithTheRemedy(CancellationToken cancellationToken)
    {
        var run = await Dotcov(cancellationToken, SourceCheckout(), "check", _report, "--min-line", "40");

        await Assert.That(run.ExitCode).IsEqualTo(DotCovCli.NoDataExitCode);
        await Assert.That(run.StdOut).IsEmpty();
        await Assert.That(run.StdErr).StartsWith(Ambiguous);
    }

    [Test]
    [Arguments("--probe-sources")]
    [Arguments("--source-root tests")]
    [Arguments("--source-root ./tests/")]
    public async Task Check_ResolvedIdentity_IsAMeasuredVerdict(string flags, CancellationToken cancellationToken)
    {
        var identity = flags.Split(' ');

        var run = await Dotcov(cancellationToken, SourceCheckout(), ["check", _report, "--min-line", "40", .. identity]);

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.Output);
        await Assert.That(run.StdOut).IsEmpty();
        // The verdict alone: a resolved identity leaves no warning behind it.
        await Assert.That(run.StdErr).StartsWith("PASS: line 50.0% (min 40%)");
        await Assert.That(run.StdErr.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)).HasSingleItem();
    }

    [Test]
    public async Task SourceRoot_TheReportDoesNotDeclare_IsAUsageError(CancellationToken cancellationToken)
    {
        var run = await Dotcov(cancellationToken, _ws.Root, "check", _report, "--min-line", "40", "--source-root", "lib");

        await Assert.That(run.ExitCode).IsEqualTo(DotCovCli.UsageExitCode);
        await Assert.That(run.StdOut).IsEmpty();
        await Assert.That(run.StdErr).IsEqualTo(
            $"error: source root 'lib' is not declared by {_report} (declared: 'src', 'tests').{Environment.NewLine}");
    }

    [Test]
    [Arguments("report")]
    [Arguments("crap")]
    [Arguments("snapshot")]
    [Arguments("diff")]
    public async Task BothModesAtOnce_OrABlankRoot_IsAUsageError_OnEveryReportReadingCommand(
        string command, CancellationToken cancellationToken)
    {
        string[] paths = command is "diff" ? [_report, _report] : [_report];

        var both = await Dotcov(cancellationToken, _ws.Root, [command, .. paths, "--probe-sources", "--source-root", "src"]);
        var blank = await Dotcov(cancellationToken, _ws.Root, [command, .. paths, "--source-root", " "]);

        await Assert.That(both.ExitCode).IsEqualTo(DotCovCli.UsageExitCode);
        await Assert.That(both.StdErr).IsEqualTo(
            $"error: --probe-sources and --source-root are alternatives: pass one of them.{Environment.NewLine}");
        await Assert.That(blank.ExitCode).IsEqualTo(DotCovCli.UsageExitCode);
        await Assert.That(blank.StdErr).StartsWith("error: Invalid --source-root value: ' '");
    }

    [Test]
    public async Task Crap_DefaultMode_IsNoData_AndAnExplicitRootMeasuresIt(CancellationToken cancellationToken)
    {
        var ambiguous = await Dotcov(cancellationToken, _ws.Root, "crap", _report);
        var resolved = await Dotcov(cancellationToken, _ws.Root, "crap", _report, "--source-root", "tests");

        await Assert.That(ambiguous.ExitCode).IsEqualTo(DotCovCli.NoDataExitCode);
        await Assert.That(ambiguous.StdErr).StartsWith(
            "NODATA: worst CRAP 2.5 (max 30) - 1 file has ambiguous identity across 2 source roots;");
        await Assert.That(resolved.ExitCode).IsEqualTo(0).Because(resolved.Output);
        await Assert.That(resolved.StdErr).StartsWith("PASS: worst CRAP 2.5 (max 30)");
    }

    [Test]
    public async Task Test_DefaultMode_IsNoData_AndAnExplicitRootMeasuresIt(CancellationToken cancellationToken)
    {
        var dotnet = FakeDotnet.Write(_ws, _report);
        var environment = new Dictionary<string, string?> { ["DOTNET_HOST_PATH"] = dotnet };

        var ambiguous = await DotcovProcess.RunAsync(cancellationToken, _ws.Root, environment, "test", "--min-line", "40");
        var resolved = await DotcovProcess.RunAsync(cancellationToken, _ws.Root, environment,
            "test", "--min-line", "40", "--source-root", "tests");

        await Assert.That(ambiguous.ExitCode).IsEqualTo(DotCovCli.NoDataExitCode).Because(ambiguous.Output);
        await Assert.That(ambiguous.StdErr).StartsWith(Ambiguous);
        await Assert.That(resolved.ExitCode).IsEqualTo(0).Because(resolved.Output);
        await Assert.That(resolved.StdErr).StartsWith("PASS: line 50.0% (min 40%)");
    }

    [Test]
    public async Task Test_BranchThresholdAlone_AfterARun_LeavesLinesUnchecked(CancellationToken cancellationToken)
    {
        // The post-run half of the threshold contract, which a real test run would take minutes to show.
        var report = _ws.Write("branches.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/M.cs", c => c.Line(1, hits: 0).Branch(2, "100% (2/2)")));

        var run = await DotcovProcess.RunAsync(cancellationToken, _ws.Root,
            new Dictionary<string, string?> { ["DOTNET_HOST_PATH"] = FakeDotnet.Write(_ws, report) },
            "test", "--min-branch", "90");

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.Output);
        await Assert.That(run.StdErr).StartsWith("PASS: line 50.0% (min 0%), branch 100.0% (min 90%)");
    }

    [Test]
    public async Task Diff_AndSnapshot_ResolveWithTheSameFlags(CancellationToken cancellationToken)
    {
        var diff = await Dotcov(cancellationToken, _ws.Root, "diff", _report, _report, "--format", "json", "--source-root", "tests");
        var snapshot = await Dotcov(cancellationToken, _ws.Root,
            "snapshot", _report, "--commit", "c", "--branch", "b", "--project", "p", "--source-root", "tests");

        await Assert.That(diff.ExitCode).IsEqualTo(0).Because(diff.Output);
        await Assert.That(diff.StdOut).Contains("\"path\": \"tests/test_app.py\"");
        await Assert.That(snapshot.ExitCode).IsEqualTo(0).Because(snapshot.Output);
        await Assert.That(snapshot.StdOut).Contains("\"path\": \"tests/test_app.py\"");
    }
}
