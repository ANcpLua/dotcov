using System.Diagnostics;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

[NotInParallel(ProcessState.Environment)]
[Category("Integration")]
[Timeout(30_000)]
public sealed class RepositoryBuildTests : IDisposable
{
    private readonly TempWorkspace _workspace = TempWorkspace.Create("dotcov-build with spaces-");

    public RepositoryBuildTests() => _workspace.CreateDirectory(".fallout");

    public void Dispose() => _workspace.Dispose();

    [Test]
    [Arguments("40", "PASS:", true)]
    [Arguments("80", "FAIL:", false)]
    [Arguments("0", "DISABLED:", false)]
    public async Task Coverage_ExistingReports_PropagatesTheGateWithoutRunningTests(
        string threshold, string verdict, bool passes, CancellationToken cancellationToken)
    {
        var report = HalfCovered();

        var run = await Run(cancellationToken, "Coverage", "--reports", report, "--min-line", threshold);

        await Assert.That(run.ExitCode is 0).IsEqualTo(passes).Because(run.Output);
        await Assert.That(run.Output).Contains(verdict);
        await Assert.That(Directory.Exists(_workspace.PathOf("TestResults"))).IsFalse();
    }

    [Test]
    public async Task Coverage_MissingReport_FailsNamingTheInput(CancellationToken cancellationToken)
    {
        var missing = _workspace.PathOf("missing.xml");

        var run = await Run(cancellationToken, "Coverage", "--reports", missing);

        await Assert.That(run.ExitCode).IsNotEqualTo(0);
        await Assert.That(run.Output).Contains("error:");
        await Assert.That(run.Output).Contains(missing);
        await Assert.That(run.Output).DoesNotContain("PASS:");
    }

    [Test]
    public async Task Report_InvalidFormat_FailsInsteadOfDiscardingTheCliExitCode(CancellationToken cancellationToken)
    {
        var run = await Run(cancellationToken, "Report", "--reports", HalfCovered(), "--format", "invalid");

        await Assert.That(run.ExitCode).IsNotEqualTo(0);
        await Assert.That(run.Output).Contains("Invalid --format");
    }

    [Test]
    public async Task Crap_NoScorableMethods_FailsWithNoData(CancellationToken cancellationToken)
    {
        var run = await Run(cancellationToken, "Crap", "--reports", HalfCovered());

        await Assert.That(run.ExitCode).IsNotEqualTo(0);
        await Assert.That(run.Output).Contains("NODATA:");
    }

    [Test]
    public async Task Diff_ExplicitBeforeAndAfter_UsesBothInputs(CancellationToken cancellationToken)
    {
        var before = _workspace.Write("before.xml", Cobertura.NewDoc()
            .AddClass("src/Example.cs", c => c.Line(1, 0).Line(2, 0)));

        var run = await Run(cancellationToken, "Diff", "--before", before,
            "--reports", HalfCovered(), "--format", "json");

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.Output);
        await Assert.That(run.Output).Contains("src/Example.cs");
        await Assert.That(run.Output).Contains("\"before\": 0");
        await Assert.That(run.Output).Contains("\"after\": 50");
        await Assert.That(run.Output).Contains("\"delta\": 50");
    }

    private string HalfCovered() => _workspace.Write("reports/dotcov.coverage.cobertura.123.xml",
        Cobertura.NewDoc().AddClass("src/Example.cs", c => c.Line(1, 1).Line(2, 0)));

    private async Task<(int ExitCode, string Output)> Run(CancellationToken cancellationToken, params string[] args)
    {
        var directory = AppContext.BaseDirectory;
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _workspace.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[]
        {
            "exec", "--depsfile", Path.Combine(directory, "DotCov.Tests.deps.json"),
            "--runtimeconfig", Path.Combine(directory, "DotCov.Tests.runtimeconfig.json"),
            Path.Combine(directory, "DotCov.Build.dll")
        }.Concat(args).Concat(["--root", _workspace.Root, "--no-logo"]))
            start.ArgumentList.Add(argument);
        start.Environment.Remove("GITHUB_STEP_SUMMARY");
        start.Environment["NO_COLOR"] = "1";

        var run = await ChildProcess.RunAsync(start, cancellationToken);
        return (run.ExitCode, AnsiStrip.From(run.Output));
    }
}
