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

    /// <summary>True when line coverage was measured and fell below <see cref="MinLinePercent"/>.</summary>
    /// <remarks>
    /// The structured counterpart to the prose in <see cref="Reason"/>: branch on this, not on
    /// wording. Same comparison <see cref="CoverageReport.Evaluate"/> uses, so the two never drift.
    /// </remarks>
    public bool LineBelowThreshold => LineRate is { } l && l * 100 < MinLinePercent;

    /// <summary>True when a branch threshold was armed, branch coverage was measured, and it fell below <see cref="MinBranchPercent"/>.</summary>
    public bool BranchBelowThreshold => MinBranchPercent > 0 && BranchRate is { } b && b * 100 < MinBranchPercent;

    /// <summary>One-line human summary, e.g. <c>FAIL: line 62.0% (min 80%), branch 50.5% (min 70%) - line coverage below threshold</c>.</summary>
    /// <remarks>
    /// Invariant-formatted: CI logs and scripts read this line, so a de-AT host must not turn
    /// <c>62.0%</c> into <c>62,0%</c>.
    /// </remarks>
    public override string ToString()
    {
        var line = LineRate is { } l ? Invariant($"{l * 100:F1}%") : "n/a";
        var branch = BranchRate is { } b ? Invariant($"{b * 100:F1}%") : "n/a";
        return Invariant($"{Outcome.ToString().ToUpperInvariant()}: line {line} (min {MinLinePercent}%), ") +
               Invariant($"branch {branch} (min {MinBranchPercent}%) - {Reason}");
    }
}
