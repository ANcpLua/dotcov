using static System.FormattableString;

namespace DotCov;

/// <summary>
/// What a threshold check concluded. A bool cannot express the difference between "coverage is
/// too low", "nothing was measured", and "no threshold was set" — yet those demand opposite
/// responses from CI, and collapsing them is how a green build comes to mean nothing.
/// </summary>
public enum GateOutcome
{
    /// <summary>Coverage was measured and cleared every threshold.</summary>
    Pass,

    /// <summary>Coverage was measured and fell short. The only outcome that means "fix the code".</summary>
    Fail,

    /// <summary>
    /// The question is unanswerable: the report carries no data for something a threshold asked
    /// about. Not a pass — a gate that cannot see cannot vouch for anything.
    /// </summary>
    NoData,

    /// <summary>
    /// Every threshold was 0, so no input could have failed. Reported distinctly because a
    /// disabled gate and a passing gate are indistinguishable from the outside, which is exactly
    /// how a `--min-line 0` survives in CI looking like enforcement.
    /// </summary>
    Disabled,
}

/// <summary>
/// The verdict of <see cref="CoverageReport.Evaluate"/>: the outcome, the rates it was reached
/// from (null where unmeasured), the thresholds it was judged against, and why.
/// </summary>
/// <remarks>
/// Deliberately carries no exit code and no severity. Whether <see cref="GateOutcome.NoData"/>
/// should fail a build, warn, or be ignored is a policy decision belonging to whatever is driving
/// the gate; this type only reports what is true.
/// </remarks>
public readonly record struct GateResult(
    GateOutcome Outcome,
    double? LineRate,
    double? BranchRate,
    double MinLinePercent,
    double MinBranchPercent,
    string Reason)
{
    /// <summary>True only for <see cref="GateOutcome.Pass"/>.</summary>
    /// <remarks>
    /// <see cref="GateOutcome.Disabled"/> is deliberately not a pass: nothing was verified, so
    /// there is nothing to affirm. Callers that want "did not fail" must say so explicitly.
    /// </remarks>
    public bool IsPass => Outcome is GateOutcome.Pass;

    /// <summary>True when the gate produced no verdict either way — nothing measured, or nothing asked.</summary>
    public bool IsInconclusive => Outcome is GateOutcome.NoData or GateOutcome.Disabled;

    /// <summary>
    /// Absorbs the binary-floating-point error of <c>rate * 100</c> so an exactly-met threshold
    /// passes: (29.0/100) * 100 computes to 28.999999999999996 in IEEE 754, which a naive
    /// comparison reads as "below 29". Small enough that a genuinely-below rate
    /// (5799/10000 against a minimum of 58) still fails.
    /// </summary>
    internal const double RateEpsilon = 1e-9;

    /// <summary>
    /// The one threshold comparison. <see cref="CoverageReport.Evaluate"/>,
    /// <see cref="CoverageReport.BelowPercent"/>, <see cref="LineBelowThreshold"/>, and
    /// <see cref="BranchBelowThreshold"/> all route through here so verdict and display
    /// can never drift.
    /// </summary>
    internal static bool MeetsThreshold(double rate, double minPercent) =>
        rate * 100 >= minPercent - RateEpsilon;

    /// <summary>True when line coverage was measured and fell below <see cref="MinLinePercent"/>.</summary>
    /// <remarks>
    /// The structured counterpart to the prose in <see cref="Reason"/>: branch on this, not on
    /// wording. Same comparison <see cref="CoverageReport.Evaluate"/> uses, so the two never drift.
    /// </remarks>
    public bool LineBelowThreshold => LineRate is { } l && !MeetsThreshold(l, MinLinePercent);

    /// <summary>True when a branch threshold was armed, branch coverage was measured, and it fell below <see cref="MinBranchPercent"/>.</summary>
    public bool BranchBelowThreshold => MinBranchPercent > 0 && BranchRate is { } b && !MeetsThreshold(b, MinBranchPercent);

    /// <summary>One-line human summary, e.g. <c>FAIL: line 62.0% (min 80%), branch 50.5% (min 70%) - line coverage below threshold</c>.</summary>
    /// <remarks>
    /// Invariant-formatted: CI logs and scripts read this line, so a de-AT host must not turn
    /// <c>62.0%</c> into <c>62,0%</c>. A dimension that fell short rounds toward the verdict
    /// (floor), so a FAIL never prints a rate that reads as equal to the minimum it missed —
    /// 1999/2500 against a minimum of 80 renders <c>79.9%</c>, not the self-contradictory
    /// <c>FAIL: line 80.0% (min 80%)</c>.
    /// </remarks>
    public override string ToString()
    {
        var line = FormatRate(LineRate, LineBelowThreshold);
        var branch = FormatRate(BranchRate, BranchBelowThreshold);
        return Invariant($"{Outcome.ToString().ToUpperInvariant()}: line {line} (min {MinLinePercent}%), ") +
               Invariant($"branch {branch} (min {MinBranchPercent}%) - {Reason}");
    }

    private static string FormatRate(double? rate, bool belowThreshold)
    {
        if (rate is not { } r) return "n/a";
        var pct = r * 100;
        // Floor at one decimal for a failing dimension; the epsilon keeps float noise a hair
        // under a tenth boundary (61.999999999999996) from flooring an extra tenth down.
        if (belowThreshold) pct = Math.Floor(pct * 10 + RateEpsilon) / 10;
        return Invariant($"{pct:F1}%");
    }
}
