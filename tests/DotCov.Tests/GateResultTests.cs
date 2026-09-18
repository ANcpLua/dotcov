using DotCov.Tests.Infrastructure;

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

    [Test]
    public async Task EmptyReport_DoesNotPass()
    {
        // The headline bug: no files found => "100% line, 100% branch" => gate cleared.
        var gate = CoverageReport.Empty.Evaluate(95, 75);

        await Assert.That(gate.Outcome).IsEqualTo(GateOutcome.NoData);
        await Assert.That(gate.IsPass).IsFalse();
        await Assert.That(gate.LineRate).IsNull();
        // Unmeasured is not "below": a rate that does not exist cannot be under a threshold.
        await Assert.That(gate.LineBelowThreshold).IsFalse();
        await Assert.That(gate.BranchBelowThreshold).IsFalse();
    }

    [Test]
    public async Task BranchThreshold_WithoutBranchData_DoesNotPass()
    {
        // Line data present, branch data absent, --min-branch 75 requested. Previously passed
        // because BranchRate returned 1.0 for "no branches emitted".
        var gate = Report(hit: 8, total: 10, bHit: 0, bTotal: 0).Evaluate(50, 75);

        await Assert.That(gate.Outcome).IsEqualTo(GateOutcome.NoData);
        await Assert.That(gate.IsPass).IsFalse();
        await Assert.That(gate.LineRate).IsEqualTo(0.8);
        await Assert.That(gate.BranchRate).IsNull();
        await Assert.That(gate.BranchBelowThreshold).IsFalse();
    }

    [Test]
    public async Task BranchThresholdOfZero_WithoutBranchData_IsNotInconclusive()
    {
        // Asking nothing of branches is answerable even with no branch data - only a caller
        // that actually requested a branch guarantee is owed a NoData.
        var gate = Report(hit: 8, total: 10, bHit: 0, bTotal: 0).Evaluate(50);

        await Assert.That(gate.Outcome).IsEqualTo(GateOutcome.Pass);
    }

    [Test]
    public async Task BothThresholdsZero_ReportsDisabled()
    {
        // The Paperless case: `--coverage-min-line 0 --coverage-min-branch 0` ran for months
        // looking like a gate. A gate that cannot fail should say so rather than say "pass".
        var gate = Report(hit: 1, total: 100, bHit: 0, bTotal: 0).Evaluate(0, 0);

        await Assert.That(gate.Outcome).IsEqualTo(GateOutcome.Disabled);
        await Assert.That(gate.IsPass).IsFalse();
        await Assert.That(gate.IsInconclusive).IsTrue();
        // An unarmed branch threshold never reports "below", whatever the measured rate.
        await Assert.That(gate.BranchBelowThreshold).IsFalse();
    }

    [Test]
    public async Task Disabled_TakesPrecedenceOverNoData()
    {
        // An unarmed gate over an empty report is still first and foremost unarmed.
        await Assert.That(CoverageReport.Empty.Evaluate(0, 0).Outcome).IsEqualTo(GateOutcome.Disabled);
    }

    [Test]
    [Arguments(9, 10, 80, GateOutcome.Pass)]
    [Arguments(8, 10, 80, GateOutcome.Pass)]   // exactly at threshold clears it (binary-exact)
    [Arguments(7, 10, 80, GateOutcome.Fail)]
    // Decimal-inexact exact-threshold cases: (58.0/100)*100 computes to 57.99999999999999 in
    // IEEE 754, so a naive `rate * 100 >= min` failed a gate that was exactly met. The
    // comparison must be epsilon-tolerant — and still fail anything genuinely below.
    [Arguments(58, 100, 58, GateOutcome.Pass)]
    [Arguments(29, 50, 58, GateOutcome.Pass)]
    [Arguments(29, 100, 29, GateOutcome.Pass)]
    [Arguments(803, 1000, 80.3, GateOutcome.Pass)]
    [Arguments(57, 100, 58, GateOutcome.Fail)]
    [Arguments(5799, 10000, 58, GateOutcome.Fail)]
    public async Task LineThreshold_ComparesInclusively(int hit, int total, double min, GateOutcome expected)
    {
        await Assert.That(Report(hit, total, bHit: 0, bTotal: 0).Evaluate(min).Outcome).IsEqualTo(expected);
    }

    [Test]
    public async Task BranchThreshold_ExactlyMet_DecimalInexactRatio_Passes()
    {
        // Same float hazard on the branch dimension: 29/100 branches against --min-branch 29.
        var gate = Report(hit: 10, total: 10, bHit: 29, bTotal: 100).Evaluate(50, 29);

        await Assert.That(gate.Outcome).IsEqualTo(GateOutcome.Pass);
        await Assert.That(gate.BranchBelowThreshold).IsFalse();
    }

    [Test]
    public async Task BelowThreshold_ExactlyMetRate_IsNotBelow()
    {
        // The structured properties share the epsilon-tolerant comparison with Evaluate, so
        // an exactly-met dimension never reports "below" — verdict and structure can't drift.
        var gate = Report(hit: 58, total: 100, bHit: 29, bTotal: 100).Evaluate(58, 29);

        await Assert.That(gate.Outcome).IsEqualTo(GateOutcome.Pass);
        await Assert.That(gate.LineBelowThreshold).IsFalse();
        await Assert.That(gate.BranchBelowThreshold).IsFalse();
    }

    [Test]
    public async Task BelowPercent_ExactlyMetFile_IsNotListed()
    {
        // A file at exactly the threshold is not an offender: same comparison as the gate,
        // so `check` can never fail a report while listing zero files (or vice versa).
        var report = new CoverageReport([
            new FileCoverage("exact.cs", 29, 100, 0, 0),
            new FileCoverage("below.cs", 28, 100, 0, 0),
        ]);

        var below = report.BelowPercent(29).ToList();

        await Assert.That(below).HasSingleItem();
        await Assert.That(below[0].Path).IsEqualTo("below.cs");
    }

    [Test]
    public async Task ToString_FailingRate_RoundsTowardVerdict()
    {
        // 1999/2500 = 79.96%. F1-rounding to nearest rendered the self-contradictory
        // "FAIL: line 80.0% (min 80%)"; a failing dimension must floor so the printed rate
        // never reads as equal to the minimum it fell short of.
        var gate = Report(1999, 2500, 0, 0).Evaluate(80);

        await Assert.That(gate.Outcome).IsEqualTo(GateOutcome.Fail);
        await Assert.That(gate.ToString()).Contains("line 79.9%");
        await Assert.That(gate.ToString()).DoesNotContain("line 80.0%");
    }

    [Test]
    public async Task ToString_FailingExactDecimalRate_DoesNotFloorAnExtraTenth()
    {
        // Floor-toward-verdict must not eat a tenth off a rate that is already an exact
        // decimal (62/100 renders 62.0, not 61.9, despite float noise around 62.0).
        var text = Report(62, 100, 0, 0).Evaluate(80).ToString();

        await Assert.That(text).Contains("line 62.0%");
    }

    [Test]
    public async Task Fail_NamesWhichDimensionFell()
    {
        // Structured verdicts, not Reason prose: which dimension fell is a fact of the result,
        // and asserting it through wording would make every rewording a false failure.
        var lineOnly = Report(5, 10, 9, 10).Evaluate(80, 50);
        await Assert.That(lineOnly.LineBelowThreshold).IsTrue();
        await Assert.That(lineOnly.BranchBelowThreshold).IsFalse();

        var branchOnly = Report(9, 10, 5, 10).Evaluate(80, 90);
        await Assert.That(branchOnly.LineBelowThreshold).IsFalse();
        await Assert.That(branchOnly.BranchBelowThreshold).IsTrue();

        var both = Report(5, 10, 5, 10).Evaluate(80, 90);
        await Assert.That(both.LineBelowThreshold).IsTrue();
        await Assert.That(both.BranchBelowThreshold).IsTrue();
    }

    [Test]
    public async Task Pass_ReportsNothingBelowThreshold()
    {
        var gate = Report(hit: 9, total: 10, bHit: 9, bTotal: 10).Evaluate(80, 50);

        await Assert.That(gate.Outcome).IsEqualTo(GateOutcome.Pass);
        await Assert.That(gate.LineBelowThreshold).IsFalse();
        await Assert.That(gate.BranchBelowThreshold).IsFalse();
    }

    [Test]
    public async Task Reason_CanonicalProse_IsPinnedHereOnly()
    {
        // The one test allowed to know the wording. Everything else asserts structure
        // (Outcome, LineBelowThreshold, BranchBelowThreshold), so rewording the prose is a
        // one-test change instead of a scatter of false failures.
        await Assert.That(Report(1, 100, 0, 0).Evaluate(0, 0).Reason).IsEqualTo("no positive threshold - this gate cannot fail");
        await Assert.That(Report(1, 100, 0, 0).Evaluate(-5).Reason).IsEqualTo("no positive threshold - this gate cannot fail");
        await Assert.That(CoverageReport.Empty.Evaluate(95, 75).Reason).IsEqualTo("report carries no line data - nothing was measured");
        await Assert.That(Report(8, 10, 0, 0).Evaluate(50, 75).Reason).IsEqualTo("branch threshold of 75% requested but the report carries no branch data");
        await Assert.That(Report(9, 10, 9, 10).Evaluate(80, 50).Reason).IsEqualTo("thresholds met");
        await Assert.That(Report(5, 10, 9, 10).Evaluate(80, 50).Reason).IsEqualTo("line coverage below threshold");
        await Assert.That(Report(9, 10, 5, 10).Evaluate(80, 90).Reason).IsEqualTo("branch coverage below threshold");
        await Assert.That(Report(5, 10, 5, 10).Evaluate(80, 90).Reason).IsEqualTo("line and branch coverage below threshold");
    }

    [Test]
    public async Task ToString_RendersUnmeasuredAsNotApplicable()
    {
        // Never "0.0%" and never "100.0%" - both are claims about data that does not exist.
        var text = CoverageReport.Empty.Evaluate(95, 75).ToString();

        await Assert.That(text).Contains("NODATA");
        await Assert.That(text).Contains("line n/a");
        await Assert.That(text).Contains("branch n/a");
    }

    [Test]
    public async Task ToString_RendersMeasuredRatesAsPercentages()
    {
        // The measured arm of both rate renderings: 62/100 lines, 101/200 branches.
        // Literal expectations on purpose - the output is invariant-formatted, so these
        // strings are exact on every host locale.
        var text = Report(62, 100, 101, 200).Evaluate(80, 70).ToString();

        await Assert.That(text).Contains("FAIL");
        await Assert.That(text).Contains("line 62.0%");
        await Assert.That(text).Contains("branch 50.5%");
    }

    [Test]
    public async Task ToString_IsCultureInvariant()
    {
        // A comma-decimal host (de-AT) writes 62,0 for 62.0 under current-culture formatting.
        // CI logs and scripts parse this line, so its shape must not follow the machine's
        // locale. Built by cloning the invariant culture instead of `new CultureInfo("de-AT")`
        // so the test also runs under invariant-globalization runtimes (Alpine/ICU-less
        // containers), where constructing a named culture throws CultureNotFoundException.
        var text = CultureScope.RenderWithCommaDecimal(
            () => Report(62, 100, 101, 200).Evaluate(80.5, 70).ToString());

        await Assert.That(text).Contains("line 62.0%");
        await Assert.That(text).Contains("branch 50.5%");
        await Assert.That(text).Contains("min 80.5%");
    }

    [Test]
    public async Task BelowPercent_OmitsUnmeasuredFiles()
    {
        // An unmeasured file is not "below" a threshold; listing it as an offender sends people
        // to write tests for a file the report has nothing to say about.
        var report = new CoverageReport([
            new FileCoverage("measured.cs", 5, 10, 0, 0),
            new FileCoverage("unmeasured.cs", 0, 0, 0, 0),
        ]);

        var below = report.BelowPercent(80).ToList();

        await Assert.That(below).HasSingleItem();
        await Assert.That(below[0].Path).IsEqualTo("measured.cs");
    }

    [Test]
    public async Task TestCodeExclusion_CatchesTestSupport_NotOnlyDotTests()
    {
        // ".Tests" does not match "TestSupport" - the exact gap that let shared fixtures count
        // as product code in a real pipeline.
        var report = new CoverageReport([
            new FileCoverage("/repo/src/Product.cs", 9, 10, 0, 0),
            new FileCoverage("/repo/Paperless.Tests/ThingTests.cs", 1, 10, 0, 0),
            new FileCoverage("/repo/Paperless.TestSupport/Fixture.cs", 1, 10, 0, 0),
        ]);

        var filtered = report.Exclude(ExclusionRules.TestCode);

        await Assert.That(filtered.Files).HasSingleItem();
        await Assert.That(filtered.Files[0].Path).IsEqualTo("/repo/src/Product.cs");
    }

    [Test]
    public async Task Evaluate_RateExactlyOnEpsilonBoundary_Passes()
    {
        // Both sides compare as exactly 25.0 in IEEE 754, so equality must pass.
        var gate = Reports.Single("a.cs", hit: 25, total: 100).Evaluate(25.0 + 1e-9);

        await Assert.That(gate.Outcome).IsEqualTo(GateOutcome.Pass);
        await Assert.That(gate.IsPass).IsTrue();
    }

    [Test]
    public async Task BranchBelowThreshold_UnarmedGate_NeverReportsBelow()
    {
        // A disabled branch gate cannot fail, even when supplied an out-of-range rate.
        var gate = new GateResult(GateOutcome.Pass, 1.0, -0.5, 80, 0, "unarmed");

        await Assert.That(gate.BranchBelowThreshold).IsFalse();
    }
}
