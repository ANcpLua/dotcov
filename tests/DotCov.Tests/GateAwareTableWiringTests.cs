using System.Diagnostics;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;
/// <summary>
/// The two table surfaces that sit next to a gate verdict, observed through their real
/// processes: the Fallout <c>ReportCoverage</c> target (table is its default format) and
/// <c>dotcov test</c>. Both print the table and then the verdict from one evaluation, so a
/// failing dimension's TOTAL must floor like the verdict does: 19999/20000 against a minimum
/// of 100 reads 99.9% in the table above <c>FAIL: line 99.9% (min 100%)</c>, never 100.0%.
/// </summary>
[TestClass]
[ResourceLock(ProcessState.Environment)]
[TestCategory("Integration")]
public sealed class GateAwareTableWiringTests : IDisposable
{
    private readonly TestContext _testContext;
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-gate-table-");
    public GateAwareTableWiringTests(TestContext testContext)
    {
        _testContext = testContext;
        _ws.CreateDirectory(".fallout");
    }

    public void Dispose() => _ws.Dispose();
    private static Cobertura NearlyFull() => Cobertura.NewDoc().AddClass("src/A.cs", c =>
    {
        for (var i = 1; i <= 20000; i++)
            c.Line(i, hits: i <= 19999 ? 1 : 0);
    });
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task FalloutTable_FailingLineGate_TotalReadsAsTheVerdict()
    {
        var cancellationToken = _testContext.CancellationToken;
        _ws.Write("TestResults/run/coverage.cobertura.xml", NearlyFull());
        var run = await FalloutBuild(cancellationToken, "--coverage-min-line", "100");
        Assert.AreNotEqual(0, run.ExitCode);
        Assert.Contains("Coverage below threshold: FAIL: line 99.9% (min 100%)", run.Output);
        Assert.Contains("19999/20000     99.9%", TotalRow(run.Output));
        Assert.DoesNotContain("100.0%", run.Output);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task FalloutTable_PassingGate_KeepsNormalRounding()
    {
        var cancellationToken = _testContext.CancellationToken;
        _ws.Write("TestResults/run/coverage.cobertura.xml", NearlyFull());
        var run = await FalloutBuild(cancellationToken, "--coverage-min-line", "99");
        Assert.AreEqual(0, run.ExitCode, message: run.Output);
        Assert.Contains("19999/20000    100.0%", TotalRow(run.Output));
        Assert.Contains("Coverage gate passed: PASS: line 100.0% (min 99%)", run.Output);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task DotcovTest_FailingLineGate_TotalReadsAsTheVerdict()
    {
        var cancellationToken = _testContext.CancellationToken;
        var dotnet = FakeDotnet.Write(_ws, _ws.Write("fixture/nearly-full.xml", NearlyFull()));
        var run = await DotcovTest(dotnet, cancellationToken, "--min-line", "100");
        Assert.AreEqual(1, run.ExitCode, message: run.Output);
        Assert.Contains("19999/20000     99.9%", TotalRow(run.StdOut));
        Assert.DoesNotContain("100.0%", run.StdOut);
        Assert.StartsWith("FAIL: line 99.9% (min 100%)", run.StdErr);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task DotcovTest_PassingGate_KeepsNormalRounding()
    {
        var cancellationToken = _testContext.CancellationToken;
        var dotnet = FakeDotnet.Write(_ws, _ws.Write("fixture/nearly-full.xml", NearlyFull()));
        var run = await DotcovTest(dotnet, cancellationToken, "--min-line", "99");
        Assert.AreEqual(0, run.ExitCode, message: run.Output);
        Assert.Contains("19999/20000    100.0%", TotalRow(run.StdOut));
        Assert.StartsWith("PASS: line 100.0% (min 99%)", run.StdErr);
    }

    private static string TotalRow(string output) => Assert.ContainsSingle(static line => line.TrimStart().StartsWith("TOTAL", StringComparison.Ordinal), output.Split('\n'));
    private async Task<ChildRun> DotcovTest(string dotnet, CancellationToken cancellationToken, params string[] args)
    {
        var directory = AppContext.BaseDirectory;
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _ws.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[]
        {
            "exec",
            "--depsfile",
            Path.Combine(directory, "DotCov.Tests.deps.json"),
            "--runtimeconfig",
            Path.Combine(directory, "DotCov.Tests.runtimeconfig.json"),
            Path.Combine(directory, "DotCov.Tool.dll"),
            "test"
        }.Concat(args))
            start.ArgumentList.Add(argument);
        start.Environment.Remove("DOTNET_TEST_RUNNER");
        start.Environment.Remove("GITHUB_STEP_SUMMARY");
        start.Environment["NO_COLOR"] = "1";
        start.Environment["DOTNET_HOST_PATH"] = dotnet;
        return await ChildProcess.RunAsync(start, cancellationToken);
    }

    private async Task<ChildRun> FalloutBuild(CancellationToken cancellationToken, params string[] args)
    {
        var directory = AppContext.BaseDirectory;
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _ws.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[]
        {
            "exec",
            "--depsfile",
            Path.Combine(directory, "DotCov.Tests.deps.json"),
            "--runtimeconfig",
            Path.Combine(directory, "DotCov.Tests.runtimeconfig.json"),
            Path.Combine(directory, "DotCov.Fallout.TestBuild.dll"),
            "ReportCoverage",
            "--root",
            _ws.Root,
            "--no-logo"
        }.Concat(args))
            start.ArgumentList.Add(argument);
        start.Environment["NO_COLOR"] = "1";
        start.Environment.Remove("GITHUB_STEP_SUMMARY");
        start.Environment.Remove("DOTCOV_TESTBUILD");
        start.Environment.Remove("DOTCOV_TESTBUILD_READY_PIPE");
        var run = await ChildProcess.RunAsync(start, cancellationToken);
        return run with
        {
            StdOut = AnsiStrip.From(run.StdOut),
            StdErr = AnsiStrip.From(run.StdErr)
        };
    }
}
