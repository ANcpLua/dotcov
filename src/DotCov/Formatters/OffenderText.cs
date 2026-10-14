using static System.FormattableString;

namespace DotCov.Formatters;

/// <summary>
/// The offender wording the table and markdown share, so both say the same thing about the same
/// file: which threshold it fell below, its rates, and what is left to cover.
/// </summary>
internal static class OffenderText
{
    /// <summary>The failed dimensions an offender list covers: <c>line</c>, <c>branch</c>, or <c>line or branch</c>.</summary>
    internal static string Dimensions(GateResult gate) => (gate.LineBelowThreshold, gate.BranchBelowThreshold) switch
    {
        (true, true) => "line or branch",
        (true, false) => "line",
        _ => "branch"
    };

    /// <summary>
    /// A rate in a dimension the gate failed, for a file below that minimum, floors like the
    /// verdict; every other rate keeps ordinary rounding.
    /// </summary>
    internal static bool LineFloors(FileCoverage f, GateResult gate) =>
        gate.LineBelowThreshold && f.LineRate is { } r && !GateResult.MeetsThreshold(r, gate.MinLinePercent);

    /// <inheritdoc cref="LineFloors"/>
    internal static bool BranchFloors(FileCoverage f, GateResult gate) =>
        gate.BranchBelowThreshold && f.BranchRate is { } r && !GateResult.MeetsThreshold(r, gate.MinBranchPercent);

    /// <summary>The file's measured rates, e.g. <c>57.1% lines  16.6% branches</c>; an unmeasured dimension is left out.</summary>
    internal static string Rates(FileCoverage f, GateResult gate, string separator)
    {
        List<string> parts = [];
        if (f.LineRate is { } line) parts.Add($"{Percent(line, LineFloors(f, gate))} lines");
        if (f.BranchRate is { } branch) parts.Add($"{Percent(branch, BranchFloors(f, gate))} branches");
        return string.Join(separator, parts);
    }

    /// <summary>What is left to cover, e.g. <c>uncovered 21,23-24 · branches 20 (1/2)</c>; empty for a count-only file.</summary>
    internal static string Detail(FileCoverage f)
    {
        List<string> parts = [];
        if (f.UncoveredLines.Count > 0) parts.Add($"uncovered {LineRanges.Format(f.UncoveredLines)}");
        if (f.PartialBranches.Count > 0) parts.Add($"branches {LineRanges.Branches(f.PartialBranches)}");
        return string.Join(" · ", parts);
    }

    private static string Percent(double rate, bool floor) =>
        Invariant($"{(floor ? GateResult.FloorToTenth(rate * 100) : rate * 100):F1}%");
}
