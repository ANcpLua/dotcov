using DotCov.Formatters;
using DotCov.Tests.Infrastructure;
using Xunit;

namespace DotCov.Tests;

/// <summary>
/// The <see cref="MarkdownFormatter.Format(CoverageReport, GateResult)"/> overload: badge and
/// verdict come from the precomputed <see cref="GateResult"/> (no re-evaluation), and any
/// dimension the gate failed renders floored — mirroring <see cref="GateResult.ToString"/> —
/// so the markdown body can never round a failing 79.96% up to a self-contradictory 80.0%
/// beside a <c>FAIL … 79.9%</c> verdict line.
/// </summary>
public sealed class FormatterGateTests
{
    [Fact]
    public void GateOverload_FailingLineDimension_FloorsHeadlineAndOffenderRow()
    {
        // 1999/2500 = 79.96%: naive F1 rounding renders 80.0% — reading as equal to the
        // minimum it missed. The gate overload floors both the headline and the file row.
        var report = Reports.Single("src/App.cs", hit: 1999, total: 2500);
        var gate = report.Evaluate(80);

        var md = MarkdownFormatter.Format(report, gate);

        Assert.Contains("## Coverage Report ❌", md);
        Assert.Contains("**Line coverage:** 79.9% (1999/2500)", md);
        Assert.Contains("| `src/App.cs` | 1999/2500 | 79.9% | - | - |", md);
        Assert.DoesNotContain("80.0%", md);
    }

    [Fact]
    public void GateOverload_FailingGate_DoesNotFloorPassingFileRates()
    {
        // Flooring is scoped to rates genuinely below the missed minimum: the 2499/2500
        // file clears 80% and must render 100.0%, not a blanket-floored 99.9%. The report
        // total (2599/5000 = 51.98%) fails, so the headline floors to 51.9% (not 52.0%).
        var report = new CoverageReport([
            new FileCoverage("src/High.cs", 2499, 2500, 0, 0),
            new FileCoverage("src/Low.cs", 100, 2500, 0, 0)
        ]);
        var gate = report.Evaluate(80);

        var md = MarkdownFormatter.Format(report, gate);

        Assert.Contains("**Line coverage:** 51.9% (2599/5000)", md);
        Assert.Contains("| `src/High.cs` | 2499/2500 | 100.0% |", md);
        Assert.DoesNotContain("99.9%", md);
        Assert.DoesNotContain("52.0%", md);
    }

    [Fact]
    public void GateOverload_FailingBranchDimension_FloorsBranchRatesOnly()
    {
        var report = Reports.Single("src/B.cs", hit: 100, total: 100, bHit: 1999, bTotal: 2500);
        var gate = report.Evaluate(minLinePercent: 10, minBranchPercent: 80);

        var md = MarkdownFormatter.Format(report, gate);

        Assert.Contains("## Coverage Report ❌", md);
        Assert.Contains("**Line coverage:** 100.0% (100/100)", md); // passing dimension untouched
        Assert.Contains("**Branch coverage:** 79.9% (1999/2500)", md);
        Assert.Contains("| `src/B.cs` | 100/100 | 100.0% | 1999/2500 | 79.9% |", md);
    }

    [Fact]
    public void GateOverload_RendersBothThresholdsFromGate()
    {
        var report = Reports.Single("src/B.cs", hit: 100, total: 100, bHit: 2400, bTotal: 2500);
        var gate = report.Evaluate(minLinePercent: 10, minBranchPercent: 80);

        var md = MarkdownFormatter.Format(report, gate);

        Assert.Contains("**Threshold:** line 10%, branch 80%", md);
    }

    [Fact]
    public void GateOverload_NoData_RendersWarningBadgeAndNoVerdictBlock()
    {
        var gate = CoverageReport.Empty.Evaluate(80);

        var md = MarkdownFormatter.Format(CoverageReport.Empty, gate);

        Assert.Contains("## Coverage Report ⚠️", md);
        Assert.Contains("> **No verdict:** report carries no line data - nothing was measured.", md);
        Assert.Contains("**Line coverage:** no data", md);
    }

    [Fact]
    public void GateOverload_Disabled_RendersWarningBadgeAndNoVerdictBlock()
    {
        var gate = Reports.Mixed.Evaluate(0);

        var md = MarkdownFormatter.Format(Reports.Mixed, gate);

        Assert.Contains("## Coverage Report ⚠️", md);
        Assert.Contains("> **No verdict:** no positive threshold - this gate cannot fail.", md);
        Assert.Contains("**Threshold:** line 0%, branch 0%", md);
    }

    [Fact]
    public void GateOverload_Pass_RendersCheckBadgeWithUnflooredRates()
    {
        var gate = Reports.FullyCovered.Evaluate(80);

        var md = MarkdownFormatter.Format(Reports.FullyCovered, gate);

        Assert.Contains("## Coverage Report ✅", md);
        Assert.Contains("**Line coverage:** 100.0% (10/10)", md);
        Assert.DoesNotContain("No verdict", md);
    }

    [Fact]
    public void GateOverload_UsesPrecomputedOutcome_WithoutReEvaluating()
    {
        // The overload's contract is presentation-only: a gate whose verdict disagrees with
        // what re-evaluating the report would produce still renders the gate's own outcome.
        var gate = new GateResult(GateOutcome.Fail, LineRate: 1.0, BranchRate: null,
            MinLinePercent: 80, MinBranchPercent: 0, Reason: "line coverage below threshold");

        var md = MarkdownFormatter.Format(Reports.FullyCovered, gate);

        Assert.Contains("## Coverage Report ❌", md);
    }
}
