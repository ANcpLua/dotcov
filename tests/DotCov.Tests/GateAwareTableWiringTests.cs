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
[NotInParallel(ProcessState.Environment)]
[Category("Integration")]
[Timeout(60_000)]
public sealed class GateAwareTableWiringTests : IDisposable
{
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-gate-table-");

    public GateAwareTableWiringTests() => _ws.CreateDirectory(".fallout");

    public void Dispose() => _ws.Dispose();

    private static Cobertura NearlyFull() => Cobertura.NewDoc().AddClass("src/A.cs", c =>
    {
        for (var i = 1; i <= 20000; i++) c.Line(i, hits: i <= 19999 ? 1 : 0);
    });

    [Test]
    public async Task FalloutTable_FailingLineGate_TotalReadsAsTheVerdict(CancellationToken cancellationToken)
    {
        _ws.Write("TestResults/run/coverage.cobertura.xml", NearlyFull());

        var run = await FalloutBuild(cancellationToken, "--coverage-min-line", "100");

        await Assert.That(run.ExitCode).IsNotEqualTo(0);
        await Assert.That(run.Output).Contains("Coverage below threshold: FAIL: line 99.9% (min 100%)");
        await Assert.That(TotalRow(run.Output)).Contains("19999/20000     99.9%");
        await Assert.That(run.Output).DoesNotContain("100.0%");
    }

    [Test]
    public async Task FalloutTable_PassingGate_KeepsNormalRounding(CancellationToken cancellationToken)
    {
        _ws.Write("TestResults/run/coverage.cobertura.xml", NearlyFull());

        var run = await FalloutBuild(cancellationToken, "--coverage-min-line", "99");

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.Output);
        await Assert.That(TotalRow(run.Output)).Contains("19999/20000    100.0%");
        await Assert.That(run.Output).Contains("Coverage gate passed: PASS: line 100.0% (min 99%)");
    }

    [Test]
    public async Task DotcovTest_FailingLineGate_TotalReadsAsTheVerdict(CancellationToken cancellationToken)
    {
        var dotnet = FakeDotnet.Write(_ws, _ws.Write("fixture/nearly-full.xml", NearlyFull()));

        var run = await DotcovTest(cancellationToken, dotnet, "--min-line", "100");

        await Assert.That(run.ExitCode).IsEqualTo(1).Because(run.Output);
        await Assert.That(TotalRow(run.StdOut)).Contains("19999/20000     99.9%");
        await Assert.That(run.StdOut).DoesNotContain("100.0%");
        await Assert.That(run.StdErr).StartsWith("FAIL: line 99.9% (min 100%)");
    }

    [Test]
    public async Task DotcovTest_PassingGate_KeepsNormalRounding(CancellationToken cancellationToken)
    {
        var dotnet = FakeDotnet.Write(_ws, _ws.Write("fixture/nearly-full.xml", NearlyFull()));

        var run = await DotcovTest(cancellationToken, dotnet, "--min-line", "99");

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.Output);
        await Assert.That(TotalRow(run.StdOut)).Contains("19999/20000    100.0%");
        await Assert.That(run.StdOut).Contains("PASS: line 100.0% (min 99%)");
    }

    private static string TotalRow(string output) =>
        output.Split('\n').Single(static line => line.TrimStart().StartsWith("TOTAL", StringComparison.Ordinal));

    private async Task<ChildRun> DotcovTest(
        CancellationToken cancellationToken, string dotnet, params string[] args)
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
                     "exec", "--depsfile", Path.Combine(directory, "DotCov.Tests.deps.json"),
                     "--runtimeconfig", Path.Combine(directory, "DotCov.Tests.runtimeconfig.json"),
                     Path.Combine(directory, "DotCov.Tool.dll"), "test"
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
                     "exec", "--depsfile", Path.Combine(directory, "DotCov.Tests.deps.json"),
                     "--runtimeconfig", Path.Combine(directory, "DotCov.Tests.runtimeconfig.json"),
                     Path.Combine(directory, "DotCov.Fallout.TestBuild.dll"),
                     "ReportCoverage", "--root", _ws.Root, "--no-logo"
                 }.Concat(args))
            start.ArgumentList.Add(argument);

        start.Environment["NO_COLOR"] = "1";
        start.Environment.Remove("GITHUB_STEP_SUMMARY");
        start.Environment.Remove("DOTCOV_TESTBUILD");
        start.Environment.Remove("DOTCOV_TESTBUILD_READY_PIPE");
        var run = await ChildProcess.RunAsync(start, cancellationToken);
        return run with { StdOut = AnsiStrip.From(run.StdOut), StdErr = AnsiStrip.From(run.StdErr) };
    }
}
