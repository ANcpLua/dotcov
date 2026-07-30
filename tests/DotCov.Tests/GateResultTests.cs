using System.Globalization;
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
    // Branch counts are deliberately not defaulted: whether a case runs with or without branch
    // data decides between Pass/Fail and NoData, so it must be visible at every call site.
    private static CoverageReport Report(int hit, int total, int bHit, int bTotal) =>
        new([new FileCoverage("src/A.cs", hit, total, bHit, bTotal)]);

    [Fact]
    public void EmptyReport_DoesNotPass()
    {
        // The headline bug: no files found => "100% line, 100% branch" => gate cleared.
        var gate = CoverageReport.Empty.Evaluate(95, 75);

        Assert.Equal(GateOutcome.NoData, gate.Outcome);
        Assert.False(gate.IsPass);
        Assert.Null(gate.LineRate);
        // Unmeasured is not "below": a rate that does not exist cannot be under a threshold.
        Assert.False(gate.LineBelowThreshold);
        Assert.False(gate.BranchBelowThreshold);
    }

    [Fact]
    public void BranchThreshold_WithoutBranchData_DoesNotPass()
    {
        // Line data present, branch data absent, --min-branch 75 requested. Previously passed
        // because BranchRate returned 1.0 for "no branches emitted".
        var gate = Report(hit: 8, total: 10, bHit: 0, bTotal: 0).Evaluate(50, 75);

        Assert.Equal(GateOutcome.NoData, gate.Outcome);
        Assert.False(gate.IsPass);
        Assert.Equal(0.8, gate.LineRate);
        Assert.Null(gate.BranchRate);
        Assert.False(gate.BranchBelowThreshold);
    }

    [Fact]
    public void BranchThresholdOfZero_WithoutBranchData_IsNotInconclusive()
    {
        // Asking nothing of branches is answerable even with no branch data - only a caller
        // that actually requested a branch guarantee is owed a NoData.
        var gate = Report(hit: 8, total: 10, bHit: 0, bTotal: 0).Evaluate(50);

        Assert.Equal(GateOutcome.Pass, gate.Outcome);
    }

    [Fact]
    public void BothThresholdsZero_ReportsDisabled()
    {
        // The Paperless case: `--coverage-min-line 0 --coverage-min-branch 0` ran for months
        // looking like a gate. A gate that cannot fail should say so rather than say "pass".
        var gate = Report(hit: 1, total: 100, bHit: 0, bTotal: 0).Evaluate(0, 0);

        Assert.Equal(GateOutcome.Disabled, gate.Outcome);
        Assert.False(gate.IsPass);
        Assert.True(gate.IsInconclusive);
        // An unarmed branch threshold never reports "below", whatever the measured rate.
        Assert.False(gate.BranchBelowThreshold);
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
        Assert.Equal(expected, Report(hit, total, bHit: 0, bTotal: 0).Evaluate(min).Outcome);
    }

    [Fact]
    public void Fail_NamesWhichDimensionFell()
    {
        // Structured verdicts, not Reason prose: which dimension fell is a fact of the result,
        // and asserting it through wording would make every rewording a false failure.
        var lineOnly = Report(5, 10, 9, 10).Evaluate(80, 50);
        Assert.True(lineOnly.LineBelowThreshold);
        Assert.False(lineOnly.BranchBelowThreshold);

        var branchOnly = Report(9, 10, 5, 10).Evaluate(80, 90);
        Assert.False(branchOnly.LineBelowThreshold);
        Assert.True(branchOnly.BranchBelowThreshold);

        var both = Report(5, 10, 5, 10).Evaluate(80, 90);
        Assert.True(both.LineBelowThreshold);
        Assert.True(both.BranchBelowThreshold);
    }

    [Fact]
    public void Pass_ReportsNothingBelowThreshold()
    {
        var gate = Report(hit: 9, total: 10, bHit: 9, bTotal: 10).Evaluate(80, 50);

        Assert.Equal(GateOutcome.Pass, gate.Outcome);
        Assert.False(gate.LineBelowThreshold);
        Assert.False(gate.BranchBelowThreshold);
    }

    [Fact]
    public void Reason_CanonicalProse_IsPinnedHereOnly()
    {
        // The one test allowed to know the wording. Everything else asserts structure
        // (Outcome, LineBelowThreshold, BranchBelowThreshold), so rewording the prose is a
        // one-test change instead of a scatter of false failures.
        Assert.Equal("both thresholds are 0 - this gate cannot fail",
            Report(1, 100, 0, 0).Evaluate(0, 0).Reason);
        Assert.Equal("report carries no line data - nothing was measured",
            CoverageReport.Empty.Evaluate(95, 75).Reason);
        Assert.Equal("branch threshold of 75% requested but the report carries no branch data",
            Report(8, 10, 0, 0).Evaluate(50, 75).Reason);
        Assert.Equal("thresholds met",
            Report(9, 10, 9, 10).Evaluate(80, 50).Reason);
        Assert.Equal("line coverage below threshold",
            Report(5, 10, 9, 10).Evaluate(80, 50).Reason);
        Assert.Equal("branch coverage below threshold",
            Report(9, 10, 5, 10).Evaluate(80, 90).Reason);
        Assert.Equal("line and branch coverage below threshold",
            Report(5, 10, 5, 10).Evaluate(80, 90).Reason);
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
    public void ToString_RendersMeasuredRatesAsPercentages()
    {
        // The measured arm of both rate renderings: 62/100 lines, 101/200 branches.
        // Literal expectations on purpose - the output is invariant-formatted, so these
        // strings are exact on every host locale.
        var text = Report(62, 100, 101, 200).Evaluate(80, 70).ToString();

        Assert.Contains("FAIL", text);
        Assert.Contains("line 62.0%", text);
        Assert.Contains("branch 50.5%", text);
    }

    [Fact]
    public void ToString_IsCultureInvariant()
    {
        // A de-AT host writes 62,0 for 62.0 under current-culture formatting. CI logs and
        // scripts parse this line, so its shape must not follow the machine's locale.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-AT");
            var text = Report(62, 100, 101, 200).Evaluate(80.5, 70).ToString();

            Assert.Contains("line 62.0%", text);
            Assert.Contains("branch 50.5%", text);
            Assert.Contains("min 80.5%", text);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
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
