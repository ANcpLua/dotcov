using DotCov.Tests.Infrastructure;
using DotCov.Tool;

namespace DotCov.Tests;

/// <summary>
/// The threshold contract <c>check</c> and <c>test</c> share (the Fallout target's twin is in
/// <see cref="FalloutBuildTests"/>): no default threshold, one flag checks its dimension and
/// disables the other, both at 0 is DISABLED, and only finite percentages from 0 to 100 parse.
/// </summary>
public sealed class ThresholdContractTests : IDisposable
{
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-thresholds-");

    public void Dispose() => _ws.Dispose();

    private static async Task<(int Code, string StdOut, string StdErr)> Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await DotCovCli.RunAsync(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    /// <summary>100% of lines, 50% of branches.</summary>
    private string LinesFullBranchesHalf() =>
        _ws.Write("half-branches.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/B.cs", c => c.Line(1, hits: 1).Branch(2, "50% (1/2)")));

    /// <summary>50% of lines, no branch data.</summary>
    private string HalfLinesOnly() =>
        _ws.Write("half-lines.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c.Line(1, hits: 1).Line(2, hits: 0)));

    [Test]
    public async Task Check_WithoutAnyThreshold_IsAUsageError_BeforeTheReportIsRead()
    {
        // The path does not exist: reading it would be exit 5. The missing threshold wins, and
        // no implicit 80 decides the gate.
        var (code, stdout, stderr) = await Run("check", _ws.PathOf("absent.xml"));

        await Assert.That(code).IsEqualTo(DotCovCli.UsageExitCode);
        await Assert.That(stdout).IsEmpty();
        await Assert.That(stderr).IsEqualTo(
            "error: missing threshold: 'check' needs --min-line N, --min-branch N, or both (0 to 100; 0 disables a dimension)." +
            Environment.NewLine);
    }

    [Test]
    public async Task Test_WithoutAnyThreshold_IsAUsageError_BeforeDotnetTestStarts()
    {
        var (code, stdout, stderr) = await Run("test", "App.Tests.csproj");

        await Assert.That(code).IsEqualTo(DotCovCli.UsageExitCode);
        // No echoed "> dotnet test" line: nothing was started.
        await Assert.That(stdout).IsEmpty();
        await Assert.That(stderr).StartsWith("error: missing threshold: 'test' needs --min-line N, --min-branch N, or both");
    }

    [Test]
    public async Task Check_BranchThresholdAlone_LeavesTheLineDimensionUnchecked()
    {
        // 50% of lines would fail any line minimum; the line dimension was not asked about.
        var path = _ws.Write("mixed.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/M.cs", c => c.Line(1, hits: 0).Branch(2, "100% (2/2)")));

        var (code, _, stderr) = await Run("check", path, "--min-branch", "90");

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stderr).StartsWith("PASS: line 50.0% (min 0%), branch 100.0% (min 90%)");
    }

    [Test]
    public async Task Check_LineThresholdAlone_LeavesTheBranchDimensionUnchecked()
    {
        var (code, _, stderr) = await Run("check", LinesFullBranchesHalf(), "--min-line", "100");

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stderr).StartsWith("PASS: line 100.0% (min 100%), branch 50.0% (min 0%)");
    }

    [Test]
    public async Task Check_BranchThresholdAlone_WithoutBranchData_IsNoData()
    {
        var (code, _, stderr) = await Run("check", HalfLinesOnly(), "--min-branch", "50");

        await Assert.That(code).IsEqualTo(DotCovCli.NoDataExitCode);
        await Assert.That(stderr).StartsWith("NODATA:");
    }

    [Test]
    public async Task Check_BothThresholdsZero_IsDisabled()
    {
        var (code, _, stderr) = await Run("check", HalfLinesOnly(), "--min-line", "0", "--min-branch", "0");

        await Assert.That(code).IsEqualTo(DotCovCli.DisabledExitCode);
        await Assert.That(stderr).StartsWith("DISABLED:");
    }

    [Test]
    [MatrixDataSource]
    public async Task OutOfRangeOrNonFinite_IsAUsageError(
        [Matrix("check", "test")] string command,
        [Matrix("--min-line", "--min-branch")] string flag,
        [Matrix("-1", "100.5", "1e3", "Infinity", "-Infinity", "NaN")] string value)
    {
        string[] target = command is "check" ? [HalfLinesOnly()] : [];

        var (code, stdout, stderr) = await Run([command, .. target, flag, value]);

        await Assert.That(code).IsEqualTo(DotCovCli.UsageExitCode);
        await Assert.That(stdout).IsEmpty();
        await Assert.That(stderr).IsEqualTo($"error: Invalid {flag} value: '{value}' (expected a number from 0 to 100).{Environment.NewLine}");
    }

    [Test]
    [Arguments("0")]
    [Arguments("100")]
    [Arguments("99.95")]
    public async Task BoundaryValues_Parse(string value)
    {
        var (code, _, stderr) = await Run("check", LinesFullBranchesHalf(), "--min-line", value);

        await Assert.That(code is 0 or DotCovCli.DisabledExitCode).IsTrue().Because(stderr);
    }

    [Test]
    [Arguments("150")]
    [Arguments("-5")]
    public async Task Report_ThresholdOutsideZeroToHundred_IsAUsageError(string value)
    {
        var (code, _, stderr) = await Run("report", HalfLinesOnly(), "--threshold", value);

        await Assert.That(code).IsEqualTo(DotCovCli.UsageExitCode);
        await Assert.That(stderr).StartsWith($"error: Invalid --threshold value: '{value}' (expected a number from 0 to 100).");
    }

    [Test]
    public async Task Help_ShowsThresholdsExplicitly_AndNamesNoDefault()
    {
        var (_, stdout, _) = await Run("help");

        await Assert.That(stdout).Contains("dotcov check <path> --min-line N and/or --min-branch N");
        await Assert.That(stdout).Contains("dotcov test [<project>] --min-line N and/or --min-branch N");
        await Assert.That(stdout).Contains("there is no default");
        await Assert.That(stdout).DoesNotContain("default 80");
        foreach (var example in stdout.Split('\n').Where(static l => l.TrimStart().StartsWith("dotcov check", StringComparison.Ordinal) ||
                                                                      l.TrimStart().StartsWith("dotcov test", StringComparison.Ordinal)))
            await Assert.That(example).Contains("--min-");
    }
}
