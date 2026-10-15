using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;
/// <summary>
/// The gate's honesty contract: an empty report, a branch threshold against branchless data,
/// and a 0% threshold are never a pass. Scoring any of them as one produces a green CI build
/// that has verified nothing, which is worse than a red one — it is a red one you cannot see.
/// </summary>
[TestClass]
public sealed class GateResultTests
{
    // Branch counts are deliberately not defaulted: whether a case runs with or without branch
    // data decides between Pass/Fail and NoData, so it must be visible at every call site.
    private static CoverageReport Report(int hit, int total, int bHit, int bTotal) => new([new FileCoverage("src/A.cs", hit, total, bHit, bTotal)]);
    [TestMethod]
    public void EmptyReport_DoesNotPass()
    {
        // No files found must not read as "100% line, 100% branch" and clear the gate.
        var gate = CoverageReport.Empty.Evaluate(95, 75);
        Assert.AreEqual(GateOutcome.NoData, gate.Outcome);
        Assert.IsFalse(gate.IsPass);
        Assert.IsNull(gate.LineRate);
        // Unmeasured is not "below": a rate that does not exist cannot be under a threshold.
        Assert.IsFalse(gate.LineBelowThreshold);
        Assert.IsFalse(gate.BranchBelowThreshold);
    }

    [TestMethod]
    public void BranchThreshold_WithoutBranchData_DoesNotPass()
    {
        // Line data present, branch data absent, --min-branch 75 requested.
        var gate = Report(hit: 8, total: 10, bHit: 0, bTotal: 0).Evaluate(50, 75);
        Assert.AreEqual(GateOutcome.NoData, gate.Outcome);
        Assert.IsFalse(gate.IsPass);
        Assert.AreEqual(0.8, gate.LineRate);
        Assert.IsNull(gate.BranchRate);
        Assert.IsFalse(gate.BranchBelowThreshold);
    }

    [TestMethod]
    public void BranchThresholdOfZero_WithoutBranchData_IsNotInconclusive()
    {
        // Asking nothing of branches is answerable even with no branch data - only a caller
        // that actually requested a branch guarantee is owed a NoData.
        var gate = Report(hit: 8, total: 10, bHit: 0, bTotal: 0).Evaluate(50);
        Assert.AreEqual(GateOutcome.Pass, gate.Outcome);
    }

    [TestMethod]
    public void BothThresholdsZero_ReportsDisabled()
    {
        // `--coverage-min-line 0 --coverage-min-branch 0` looks like a gate but cannot fail,
        // so it should say so rather than say "pass".
        var gate = Report(hit: 1, total: 100, bHit: 0, bTotal: 0).Evaluate(0);
        Assert.AreEqual(GateOutcome.Disabled, gate.Outcome);
        Assert.IsFalse(gate.IsPass);
        Assert.IsTrue(gate.IsInconclusive);
        // An unarmed branch threshold never reports "below", whatever the measured rate.
        Assert.IsFalse(gate.BranchBelowThreshold);
    }

    [TestMethod]
    public void Disabled_TakesPrecedenceOverNoData()
    {
        // An unarmed gate over an empty report is still first and foremost unarmed.
        Assert.AreEqual(GateOutcome.Disabled, CoverageReport.Empty.Evaluate(0).Outcome);
    }

    [TestMethod]
    [DataRow(9, 10, 80, GateOutcome.Pass)]
    [DataRow(8, 10, 80, GateOutcome.Pass)] // exactly at threshold clears it (binary-exact)
    [DataRow(7, 10, 80, GateOutcome.Fail)]
    // Decimal-inexact exact-threshold cases: (58.0/100)*100 computes to 57.99999999999999 in
    // IEEE 754, so a naive `rate * 100 >= min` would fail a gate that is exactly met. The
    // comparison must be epsilon-tolerant — and still fail anything genuinely below.
    [DataRow(58, 100, 58, GateOutcome.Pass)]
    [DataRow(29, 50, 58, GateOutcome.Pass)]
    [DataRow(29, 100, 29, GateOutcome.Pass)]
    [DataRow(803, 1000, 80.3, GateOutcome.Pass)]
    [DataRow(57, 100, 58, GateOutcome.Fail)]
    [DataRow(5799, 10000, 58, GateOutcome.Fail)]
    public void LineThreshold_ComparesInclusively(int hit, int total, double min, GateOutcome expected)
    {
        Assert.AreEqual(expected, Report(hit, total, bHit: 0, bTotal: 0).Evaluate(min).Outcome);
    }

    [TestMethod]
    public void BranchThreshold_ExactlyMet_DecimalInexactRatio_Passes()
    {
        // Same float hazard on the branch dimension: 29/100 branches against --min-branch 29.
        var gate = Report(hit: 10, total: 10, bHit: 29, bTotal: 100).Evaluate(50, 29);
        Assert.AreEqual(GateOutcome.Pass, gate.Outcome);
        Assert.IsFalse(gate.BranchBelowThreshold);
    }

    [TestMethod]
    public void BelowThreshold_ExactlyMetRate_IsNotBelow()
    {
        // The structured properties share the epsilon-tolerant comparison with Evaluate, so
        // an exactly-met dimension never reports "below" — verdict and structure can't drift.
        var gate = Report(hit: 58, total: 100, bHit: 29, bTotal: 100).Evaluate(58, 29);
        Assert.AreEqual(GateOutcome.Pass, gate.Outcome);
        Assert.IsFalse(gate.LineBelowThreshold);
        Assert.IsFalse(gate.BranchBelowThreshold);
    }

    [TestMethod]
    public void Offenders_ExactlyMetFile_IsNotListed()
    {
        // A file at exactly the threshold is not an offender: same comparison as the gate,
        // so `check` can never fail a report while listing zero files (or vice versa).
        var report = new CoverageReport([new FileCoverage("exact.cs", 29, 100, 0, 0), new FileCoverage("below.cs", 28, 100, 0, 0), ]);
        var below = report.Offenders(report.Evaluate(29));
        Assert.ContainsSingle(below);
        Assert.AreEqual("below.cs", below[0].Path);
    }

    [TestMethod]
    public void ToString_FailingRate_RoundsTowardVerdict()
    {
        // 1999/2500 = 79.96%. F1-rounding to nearest would render the self-contradictory
        // "FAIL: line 80.0% (min 80%)"; a failing dimension must floor so the printed rate
        // never reads as equal to the minimum it fell short of.
        var gate = Report(1999, 2500, 0, 0).Evaluate(80);
        Assert.AreEqual(GateOutcome.Fail, gate.Outcome);
        Assert.Contains("line 79.9%", gate.ToString());
        Assert.DoesNotContain("line 80.0%", gate.ToString());
    }

    [TestMethod]
    public void ToString_FailingExactDecimalRate_DoesNotFloorAnExtraTenth()
    {
        // Floor-toward-verdict must not eat a tenth off a rate that is already an exact
        // decimal (62/100 renders 62.0, not 61.9, despite float noise around 62.0).
        var text = Report(62, 100, 0, 0).Evaluate(80).ToString();
        Assert.Contains("line 62.0%", text);
    }

    [TestMethod]
    public void Fail_NamesWhichDimensionFell()
    {
        // Structured verdicts, not Reason prose: which dimension fell is a fact of the result,
        // and asserting it through wording would make every rewording a false failure.
        var lineOnly = Report(5, 10, 9, 10).Evaluate(80, 50);
        Assert.IsTrue(lineOnly.LineBelowThreshold);
        Assert.IsFalse(lineOnly.BranchBelowThreshold);
        var branchOnly = Report(9, 10, 5, 10).Evaluate(80, 90);
        Assert.IsFalse(branchOnly.LineBelowThreshold);
        Assert.IsTrue(branchOnly.BranchBelowThreshold);
        var both = Report(5, 10, 5, 10).Evaluate(80, 90);
        Assert.IsTrue(both.LineBelowThreshold);
        Assert.IsTrue(both.BranchBelowThreshold);
    }

    [TestMethod]
    public void Pass_ReportsNothingBelowThreshold()
    {
        var gate = Report(hit: 9, total: 10, bHit: 9, bTotal: 10).Evaluate(80, 50);
        Assert.AreEqual(GateOutcome.Pass, gate.Outcome);
        Assert.IsFalse(gate.LineBelowThreshold);
        Assert.IsFalse(gate.BranchBelowThreshold);
    }

    [TestMethod]
    public void Reason_CanonicalProse_IsPinnedHereOnly()
    {
        // The one test allowed to know the wording. Everything else asserts structure
        // (Outcome, LineBelowThreshold, BranchBelowThreshold), so rewording the prose is a
        // one-test change instead of a scatter of false failures.
        Assert.AreEqual("no positive threshold - this gate cannot fail", Report(1, 100, 0, 0).Evaluate(0).Reason);
        Assert.AreEqual("no positive threshold - this gate cannot fail", Report(1, 100, 0, 0).Evaluate(-5).Reason);
        Assert.AreEqual("report carries no line data - nothing was measured", CoverageReport.Empty.Evaluate(95, 75).Reason);
        Assert.AreEqual("branch threshold of 75% requested but the report carries no branch data", Report(8, 10, 0, 0).Evaluate(50, 75).Reason);
        Assert.AreEqual("thresholds met", Report(9, 10, 9, 10).Evaluate(80, 50).Reason);
        Assert.AreEqual("line coverage below threshold", Report(5, 10, 9, 10).Evaluate(80, 50).Reason);
        Assert.AreEqual("branch coverage below threshold", Report(9, 10, 5, 10).Evaluate(80, 90).Reason);
        Assert.AreEqual("line and branch coverage below threshold", Report(5, 10, 5, 10).Evaluate(80, 90).Reason);
    }

    [TestMethod]
    public void ToString_RendersUnmeasuredAsNotApplicable()
    {
        // Never "0.0%" and never "100.0%" - both are claims about data that does not exist.
        var text = CoverageReport.Empty.Evaluate(95, 75).ToString();
        Assert.Contains("NODATA", text);
        Assert.Contains("line n/a", text);
        Assert.Contains("branch n/a", text);
    }

    [TestMethod]
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

    [TestMethod]
    public void ToString_IsCultureInvariant()
    {
        // A comma-decimal host (de-AT) writes 62,0 for 62.0 under current-culture formatting.
        // CI logs and scripts parse this line, so its shape must not follow the machine's
        // locale. Built by cloning the invariant culture instead of `new CultureInfo("de-AT")`
        // so the test also runs under invariant-globalization runtimes (Alpine/ICU-less
        // containers), where constructing a named culture throws CultureNotFoundException.
        var text = CultureScope.RenderWithCommaDecimal(() => Report(62, 100, 101, 200).Evaluate(80.5, 70).ToString());
        Assert.Contains("line 62.0%", text);
        Assert.Contains("branch 50.5%", text);
        Assert.Contains("min 80.5%", text);
    }

    [TestMethod]
    public void Offenders_OmitUnmeasuredFiles()
    {
        // An unmeasured file is not "below" a threshold; listing it as an offender sends people
        // to write tests for a file the report has nothing to say about.
        var report = new CoverageReport([new FileCoverage("measured.cs", 5, 10, 0, 0), new FileCoverage("unmeasured.cs", 0, 0, 0, 0), ]);
        var below = report.Offenders(report.Evaluate(80));
        Assert.ContainsSingle(below);
        Assert.AreEqual("measured.cs", below[0].Path);
    }

    [TestMethod]
    public void TestCodeExclusion_CatchesTestSupport_NotOnlyDotTests()
    {
        // ".Tests" does not match "TestSupport", so without a rule of its own shared fixtures
        // would count as product code.
        var report = new CoverageReport([new FileCoverage("/repo/src/Product.cs", 9, 10, 0, 0), new FileCoverage("/repo/Paperless.Tests/ThingTests.cs", 1, 10, 0, 0), new FileCoverage("/repo/Paperless.TestSupport/Fixture.cs", 1, 10, 0, 0), ]);
        var filtered = report.Exclude(ExclusionRules.TestCode);
        Assert.ContainsSingle(filtered.Files);
        Assert.AreEqual("/repo/src/Product.cs", filtered.Files[0].Path);
    }

    [TestMethod]
    public void Evaluate_RateExactlyOnEpsilonBoundary_Passes()
    {
        // Both sides compare as exactly 25.0 in IEEE 754, so equality must pass.
        var gate = Reports.Single("a.cs", hit: 25, total: 100).Evaluate(25.0 + 1e-9);
        Assert.AreEqual(GateOutcome.Pass, gate.Outcome);
        Assert.IsTrue(gate.IsPass);
    }

    [TestMethod]
    public void BranchBelowThreshold_UnarmedGate_NeverReportsBelow()
    {
        // A disabled branch gate cannot fail, even when supplied an out-of-range rate.
        var gate = new GateResult(GateOutcome.Pass, 1.0, -0.5, 80, 0, "unarmed");
        Assert.IsFalse(gate.BranchBelowThreshold);
    }
}
