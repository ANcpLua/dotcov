using Xunit;

namespace DotCov.Tests;

/// <summary>
/// The gate's honesty contract. Every case here was a silent pass before <see cref="GateResult"/>
/// existed: an empty report scored 100%, a branch threshold against branchless data scored 100%,
/// and a 0% threshold was indistinguishable from a real one. All three produce a green CI build
/// that has verified nothing, which is worse than a red one — it is a red one you cannot see.
/// </summary>
public sealed class GateResultTests
{
    private static CoverageReport Report(int hit, int total, int bHit = 0, int bTotal = 0) =>
        new([new FileCoverage("src/A.cs", hit, total, bHit, bTotal)]);

    [Fact]
    public void EmptyReport_DoesNotPass()
    {
        // The headline bug: no files found => "100% line, 100% branch" => gate cleared.
        var gate = CoverageReport.Empty.Evaluate(95, 75);

        Assert.Equal(GateOutcome.NoData, gate.Outcome);
        Assert.False(gate.IsPass);
        Assert.Null(gate.LineRate);
        Assert.Contains("no line data", gate.Reason);
    }

    [Fact]
    public void BranchThreshold_WithoutBranchData_DoesNotPass()
    {
        // Line data present, branch data absent, --min-branch 75 requested. Previously passed
        // because BranchRate returned 1.0 for "no branches emitted".
        var gate = Report(hit: 8, total: 10).Evaluate(50, 75);

        Assert.Equal(GateOutcome.NoData, gate.Outcome);
        Assert.False(gate.IsPass);
        Assert.Equal(0.8, gate.LineRate);
        Assert.Null(gate.BranchRate);
        Assert.Contains("no branch data", gate.Reason);
    }

    [Fact]
    public void BranchThresholdOfZero_WithoutBranchData_IsNotInconclusive()
    {
        // Asking nothing of branches is answerable even with no branch data - only a caller
        // that actually requested a branch guarantee is owed a NoData.
        var gate = Report(hit: 8, total: 10).Evaluate(50);

        Assert.Equal(GateOutcome.Pass, gate.Outcome);
    }

    [Fact]
    public void BothThresholdsZero_ReportsDisabled()
    {
        // The Paperless case: `--coverage-min-line 0 --coverage-min-branch 0` ran for months
        // looking like a gate. A gate that cannot fail should say so rather than say "pass".
        var gate = Report(hit: 1, total: 100).Evaluate(0, 0);

        Assert.Equal(GateOutcome.Disabled, gate.Outcome);
        Assert.False(gate.IsPass);
        Assert.True(gate.IsInconclusive);
        Assert.Contains("cannot fail", gate.Reason);
    }

    [Fact]
    public void Disabled_TakesPrecedenceOverNoData()
    {
        // An unarmed gate over an empty report is still first and foremost unarmed.
        Assert.Equal(GateOutcome.Disabled, CoverageReport.Empty.Evaluate(0, 0).Outcome);
    }

    [Theory]
    [InlineData(9, 10, 80, GateOutcome.Pass)]
    [InlineData(8, 10, 80, GateOutcome.Pass)]   // exactly at threshold clears it
    [InlineData(7, 10, 80, GateOutcome.Fail)]
    public void LineThreshold_ComparesInclusively(int hit, int total, double min, GateOutcome expected)
    {
        Assert.Equal(expected, Report(hit, total).Evaluate(min).Outcome);
    }

    [Fact]
    public void Fail_NamesWhichDimensionFell()
    {
        Assert.Contains("line coverage below", Report(5, 10, 9, 10).Evaluate(80, 50).Reason);
        Assert.Contains("branch coverage below", Report(9, 10, 5, 10).Evaluate(80, 90).Reason);
        Assert.Contains("line and branch", Report(5, 10, 5, 10).Evaluate(80, 90).Reason);
    }

    [Fact]
    public void ToString_RendersUnmeasuredAsNotApplicable()
    {
        // Never "0.0%" and never "100.0%" - both are claims about data that does not exist.
        var text = CoverageReport.Empty.Evaluate(95, 75).ToString();

        Assert.Contains("NODATA", text);
        Assert.Contains("line n/a", text);
        Assert.Contains("branch n/a", text);
    }

    [Fact]
    public void BelowPercent_OmitsUnmeasuredFiles()
    {
        // An unmeasured file is not "below" a threshold; listing it as an offender sends people
        // to write tests for a file the report has nothing to say about.
        var report = new CoverageReport([
            new FileCoverage("measured.cs", 5, 10, 0, 0),
            new FileCoverage("unmeasured.cs", 0, 0, 0, 0),
        ]);

        var below = report.BelowPercent(80).ToList();

        Assert.Single(below);
        Assert.Equal("measured.cs", below[0].Path);
    }

    [Fact]
    public void TestCodeExclusion_CatchesTestSupport_NotOnlyDotTests()
    {
        // ".Tests" does not match "TestSupport" - the exact gap that let shared fixtures count
        // as product code in a real pipeline.
        var report = new CoverageReport([
            new FileCoverage("/repo/src/Product.cs", 9, 10, 0, 0),
            new FileCoverage("/repo/Paperless.Tests/ThingTests.cs", 1, 10, 0, 0),
            new FileCoverage("/repo/Paperless.TestSupport/Fixture.cs", 1, 10, 0, 0),
        ]);

        var filtered = report.Exclude(ExclusionRules.TestCode);

        Assert.Single(filtered.Files);
        Assert.Equal("/repo/src/Product.cs", filtered.Files[0].Path);
    }
}
