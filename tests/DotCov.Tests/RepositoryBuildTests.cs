using System.Diagnostics;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;
[TestClass]
[ResourceLock(ProcessState.Environment)]
[TestCategory("Integration")]
public sealed class RepositoryBuildTests : IDisposable
{
    private readonly TestContext _testContext;
    private readonly TempWorkspace _workspace = TempWorkspace.Create("dotcov-build with spaces-");
    public RepositoryBuildTests(TestContext testContext)
    {
        _testContext = testContext;
        _workspace.CreateDirectory(".fallout");
    }

    public void Dispose() => _workspace.Dispose();
    [TestMethod]
    [DataRow("40", "PASS:", true)]
    [DataRow("80", "FAIL:", false)]
    [DataRow("0", "DISABLED:", false)]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Coverage_ExistingReports_PropagatesTheGateWithoutRunningTests(string threshold, string verdict, bool passes)
    {
        var cancellationToken = _testContext.CancellationToken;
        var report = HalfCovered();
        var run = await Run(cancellationToken, "Coverage", "--reports", report, "--min-line", threshold, "--min-branch", "0");
        Assert.AreEqual(passes, run.ExitCode is 0, message: run.Output);
        Assert.Contains(verdict, run.Output);
        Assert.IsFalse(Directory.Exists(_workspace.PathOf("TestResults")));
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Coverage_MissingReport_FailsNamingTheInput()
    {
        var cancellationToken = _testContext.CancellationToken;
        var missing = _workspace.PathOf("missing.xml");
        var run = await Run(cancellationToken, "Coverage", "--reports", missing);
        Assert.AreNotEqual(0, run.ExitCode);
        Assert.Contains("error:", run.Output);
        Assert.Contains(missing, run.Output);
        Assert.DoesNotContain("PASS:", run.Output);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Coverage_SourceProjectMissingFromReport_FailsBeforeTheThresholds()
    {
        var cancellationToken = _testContext.CancellationToken;
        _workspace.Write("src/Tests/Tests.csproj", "<Project />");
        _workspace.Write("src/Dropped/Dropped.csproj", "<Project />");
        var run = await Run(cancellationToken, "Coverage", "--reports", HalfCovered(), "--min-line", "40", "--min-branch", "0");
        Assert.AreNotEqual(0, run.ExitCode);
        Assert.Contains("Coverage report has no package for: Dropped", run.Output);
        Assert.DoesNotContain("PASS:", run.Output);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Coverage_EverySourceProjectMeasured_ReachesTheThresholds()
    {
        var cancellationToken = _testContext.CancellationToken;
        _workspace.Write("src/Tests/Tests.csproj", "<Project />");
        var run = await Run(cancellationToken, "Coverage", "--reports", HalfCovered(), "--min-line", "40", "--min-branch", "0");
        Assert.AreEqual(0, run.ExitCode, message: run.Output);
        Assert.Contains("PASS:", run.Output);
    }

    [TestMethod]
    [DataRow("40", true, "50.0%", "brightgreen")]
    [DataRow("80", false, "50.0%", "red")]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Coverage_BadgeDirectory_DrawsTheGateVerdict(string threshold, bool passes, string message, string color)
    {
        var cancellationToken = _testContext.CancellationToken;
        var run = await Run(cancellationToken, "Coverage", "--reports", HalfCovered(), "--min-line", threshold, "--min-branch", "0", "--badge-directory", "out/badge");
        Assert.AreEqual(passes, run.ExitCode is 0, message: run.Output);
        await AssertBadge("out/badge", message, color, passes ? "PASS:" : "FAIL:");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Coverage_SourceProjectMissing_DrawsNoDataInsteadOfTheThresholds()
    {
        var cancellationToken = _testContext.CancellationToken;
        // The rest of the report clears the thresholds; without Dropped they judged only part of src.
        _workspace.Write("src/Tests/Tests.csproj", "<Project />");
        _workspace.Write("src/Dropped/Dropped.csproj", "<Project />");
        var run = await Run(cancellationToken, "Coverage", "--reports", HalfCovered(), "--min-line", "40", "--min-branch", "0", "--badge-directory", "out/badge");
        Assert.AreNotEqual(0, run.ExitCode);
        Assert.Contains("Coverage report has no package for: Dropped", run.Output);
        await AssertBadge("out/badge", "no data", "lightgrey", "NODATA: line n/a (min 40%), branch n/a (min 0%) - coverage report has no package for: Dropped");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Coverage_SourceProjectMissing_InvalidThreshold_FailsNamingIt()
    {
        var cancellationToken = _testContext.CancellationToken;
        _workspace.Write("src/Dropped/Dropped.csproj", "<Project />");
        var run = await Run(cancellationToken, "Coverage", "--reports", HalfCovered(), "--min-line", "abc", "--badge-directory", "out/badge");
        Assert.AreNotEqual(0, run.ExitCode);
        Assert.Contains("Invalid --min-line value: 'abc' (expected a number from 0 to 100).", run.Output);
        Assert.IsFalse(Directory.Exists(_workspace.PathOf("out/badge")));
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Coverage_WithoutBadgeDirectory_DrawsNoBadge()
    {
        var cancellationToken = _testContext.CancellationToken;
        var run = await Run(cancellationToken, "Coverage", "--reports", HalfCovered(), "--min-line", "40", "--min-branch", "0");
        Assert.AreEqual(0, run.ExitCode, message: run.Output);
        Assert.IsEmpty(Directory.EnumerateFiles(_workspace.Root, "coverage-badge.*", SearchOption.AllDirectories));
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Report_InvalidFormat_FailsInsteadOfDiscardingTheCliExitCode()
    {
        var cancellationToken = _testContext.CancellationToken;
        var run = await Run(cancellationToken, "Report", "--reports", HalfCovered(), "--format", "invalid");
        Assert.AreNotEqual(0, run.ExitCode);
        Assert.Contains("Invalid --format", run.Output);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Crap_NoScorableMethods_FailsWithNoData()
    {
        var cancellationToken = _testContext.CancellationToken;
        var run = await Run(cancellationToken, "Crap", "--reports", HalfCovered());
        Assert.AreNotEqual(0, run.ExitCode);
        Assert.Contains("NODATA:", run.Output);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Crap_UnscoredMethod_FailsWithNoData_ThoughEveryScoredMethodPasses()
    {
        var cancellationToken = _testContext.CancellationToken;
        // The target's gate needs every method scored: the one without complexity withholds it.
        var report = _workspace.Write("reports/dotcov.coverage.cobertura.456.xml", Cobertura.NewDoc().AddClass("src/Example.cs", "Example", c => c.Method("Scored", "()", "1", m => m.Line(1, 1)).Method("Unscored", "()", null, m => m.Line(5, 0))));
        var run = await Run(cancellationToken, "Crap", "--reports", report);
        Assert.AreNotEqual(0, run.ExitCode);
        Assert.Contains("NODATA: worst CRAP 1.0 (max 30) - 1 of 2 methods could not be scored", run.Output);
        Assert.Contains("dotcov crap exited with code 3", run.Output);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Diff_ExplicitBeforeAndAfter_UsesBothInputs()
    {
        var cancellationToken = _testContext.CancellationToken;
        var before = _workspace.Write("before.xml", Cobertura.NewDoc().AddClass("src/Example.cs", c => c.Line(1, 0).Line(2, 0)));
        var run = await Run(cancellationToken, "Diff", "--before", before, "--reports", HalfCovered(), "--format", "json");
        Assert.AreEqual(0, run.ExitCode, message: run.Output);
        Assert.Contains("src/Example.cs", run.Output);
        Assert.Contains("\"before\": 0", run.Output);
        Assert.Contains("\"after\": 50", run.Output);
        Assert.Contains("\"delta\": 50", run.Output);
    }

    /// <summary>Both files, relative to the build root, with the verdict in the SVG's title.</summary>
    private async Task AssertBadge(string directory, string message, string color, string verdict)
    {
        var svg = await File.ReadAllTextAsync(_workspace.PathOf($"{directory}/coverage-badge.svg"));
        var json = await File.ReadAllTextAsync(_workspace.PathOf($"{directory}/coverage-badge.json"));
        Assert.Contains($"<title>Coverage: {verdict}", svg);
        Assert.AreEqual($$"""{"schemaVersion":1,"label":"Coverage","message":"{{message}}","color":"{{color}}"}""", json);
    }

    private string HalfCovered() => _workspace.Write("reports/dotcov.coverage.cobertura.123.xml", Cobertura.NewDoc().AddClass("src/Example.cs", c => c.Line(1, 1).Line(2, 0)));
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
            "exec",
            "--depsfile",
            Path.Combine(directory, "DotCov.Tests.deps.json"),
            "--runtimeconfig",
            Path.Combine(directory, "DotCov.Tests.runtimeconfig.json"),
            Path.Combine(directory, "DotCov.Build.dll")
        }.Concat(args).Concat(["--root", _workspace.Root, "--no-logo"]))
            start.ArgumentList.Add(argument);
        start.Environment.Remove("GITHUB_STEP_SUMMARY");
        start.Environment["NO_COLOR"] = "1";
        var run = await ChildProcess.RunAsync(start, cancellationToken);
        return (run.ExitCode, AnsiStrip.From(run.Output));
    }
}
