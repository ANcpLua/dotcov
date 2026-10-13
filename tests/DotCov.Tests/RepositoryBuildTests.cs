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

        var run = await Run(cancellationToken, "Coverage", "--reports", report, "--min-line", threshold, "--min-branch", "0");

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
    public async Task Coverage_SourceProjectMissingFromReport_FailsBeforeTheThresholds(CancellationToken cancellationToken)
    {
        _workspace.Write("src/Tests/Tests.csproj", "<Project />");
        _workspace.Write("src/Dropped/Dropped.csproj", "<Project />");

        var run = await Run(cancellationToken, "Coverage", "--reports", HalfCovered(), "--min-line", "40", "--min-branch", "0");

        await Assert.That(run.ExitCode).IsNotEqualTo(0);
        await Assert.That(run.Output).Contains("Coverage report has no package for: Dropped");
        await Assert.That(run.Output).DoesNotContain("PASS:");
    }

    [Test]
    public async Task Coverage_EverySourceProjectMeasured_ReachesTheThresholds(CancellationToken cancellationToken)
    {
        _workspace.Write("src/Tests/Tests.csproj", "<Project />");

        var run = await Run(cancellationToken, "Coverage", "--reports", HalfCovered(), "--min-line", "40", "--min-branch", "0");

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.Output);
        await Assert.That(run.Output).Contains("PASS:");
    }

    [Test]
    [Arguments("40", true, "50.0%", "brightgreen")]
    [Arguments("80", false, "50.0%", "red")]
    public async Task Coverage_BadgeDirectory_DrawsTheGateVerdict(
        string threshold, bool passes, string message, string color, CancellationToken cancellationToken)
    {
        var run = await Run(cancellationToken, "Coverage", "--reports", HalfCovered(), "--min-line", threshold,
            "--min-branch", "0", "--badge-directory", "out/badge");

        await Assert.That(run.ExitCode is 0).IsEqualTo(passes).Because(run.Output);
        await AssertBadge("out/badge", message, color, passes ? "PASS:" : "FAIL:");
    }

    [Test]
    public async Task Coverage_SourceProjectMissing_DrawsNoDataInsteadOfTheThresholds(CancellationToken cancellationToken)
    {
        // The rest of the report clears the thresholds; without Dropped they judged only part of src.
        _workspace.Write("src/Tests/Tests.csproj", "<Project />");
        _workspace.Write("src/Dropped/Dropped.csproj", "<Project />");

        var run = await Run(cancellationToken, "Coverage", "--reports", HalfCovered(), "--min-line", "40",
            "--min-branch", "0", "--badge-directory", "out/badge");

        await Assert.That(run.ExitCode).IsNotEqualTo(0);
        await Assert.That(run.Output).Contains("Coverage report has no package for: Dropped");
        await AssertBadge("out/badge", "no data", "lightgrey",
            "NODATA: line n/a (min 40%), branch n/a (min 0%) - coverage report has no package for: Dropped");
    }

    [Test]
    public async Task Coverage_SourceProjectMissing_InvalidThreshold_FailsNamingIt(CancellationToken cancellationToken)
    {
        _workspace.Write("src/Dropped/Dropped.csproj", "<Project />");

        var run = await Run(cancellationToken, "Coverage", "--reports", HalfCovered(), "--min-line", "abc",
            "--badge-directory", "out/badge");

        await Assert.That(run.ExitCode).IsNotEqualTo(0);
        await Assert.That(run.Output).Contains("Invalid --min-line value: 'abc' (expected a number from 0 to 100).");
        await Assert.That(Directory.Exists(_workspace.PathOf("out/badge"))).IsFalse();
    }

    [Test]
    public async Task Coverage_WithoutBadgeDirectory_DrawsNoBadge(CancellationToken cancellationToken)
    {
        var run = await Run(cancellationToken, "Coverage", "--reports", HalfCovered(), "--min-line", "40", "--min-branch", "0");

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.Output);
        await Assert.That(Directory.EnumerateFiles(_workspace.Root, "coverage-badge.*", SearchOption.AllDirectories)).IsEmpty();
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
    public async Task Crap_UnscoredMethod_FailsWithNoData_ThoughEveryScoredMethodPasses(CancellationToken cancellationToken)
    {
        // The target's gate needs every method scored: the one without complexity withholds it.
        var report = _workspace.Write("reports/dotcov.coverage.cobertura.456.xml", Cobertura.NewDoc()
            .AddClass("src/Example.cs", "Example", c => c
                .Method("Scored", "()", "1", m => m.Line(1, 1))
                .Method("Unscored", "()", null, m => m.Line(5, 0))));

        var run = await Run(cancellationToken, "Crap", "--reports", report);

        await Assert.That(run.ExitCode).IsNotEqualTo(0);
        await Assert.That(run.Output).Contains("NODATA: worst CRAP 1.0 (max 30) - 1 of 2 methods could not be scored");
        await Assert.That(run.Output).Contains("dotcov crap exited with code 3");
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

    /// <summary>Both files, relative to the build root, with the verdict in the SVG's title.</summary>
    private async Task AssertBadge(string directory, string message, string color, string verdict)
    {
        var svg = await File.ReadAllTextAsync(_workspace.PathOf($"{directory}/coverage-badge.svg"));
        var json = await File.ReadAllTextAsync(_workspace.PathOf($"{directory}/coverage-badge.json"));

        await Assert.That(svg).Contains($"<title>Coverage: {verdict}");
        await Assert.That(json).IsEqualTo($$"""{"schemaVersion":1,"label":"Coverage","message":"{{message}}","color":"{{color}}"}""");
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
