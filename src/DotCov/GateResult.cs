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

    /// <summary>One-line human summary, e.g. <c>FAIL: line 62.0% &lt; 80% (line coverage below threshold)</c>.</summary>
    public override string ToString()
    {
        var line = LineRate is { } l ? $"{l * 100:F1}%" : "n/a";
        var branch = BranchRate is { } b ? $"{b * 100:F1}%" : "n/a";
        return $"{Outcome.ToString().ToUpperInvariant()}: line {line} (min {MinLinePercent}%), " +
               $"branch {branch} (min {MinBranchPercent}%) - {Reason}";
    }
}
