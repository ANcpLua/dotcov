using System.Text;
using static System.FormattableString;

namespace DotCov.Formatters;

// All numeric rendering is invariant-formatted: this output lands in CI logs, PR summaries,
// and scripts, so its shape must not follow the host locale (de-AT would write 62,1%).
public static class TableFormatter
{
    public static string Format(CoverageReport report) => Format(report, color: false);

    public static string Format(CoverageReport report, bool color) =>
        Render(report, color, lineFloorBelow: null, branchFloorBelow: null);

    /// <inheritdoc cref="Format(CoverageReport, GateResult, bool)"/>
    public static string Format(CoverageReport report, GateResult gate) => Format(report, gate, color: false);

    /// <summary>
    /// Render the report next to an already-evaluated <see cref="GateResult"/>, flooring exactly
    /// what <see cref="MarkdownFormatter.Format(CoverageReport, GateResult)"/> floors: in a
    /// dimension the gate failed, the TOTAL cell and every file cell below the missed minimum
    /// round toward the verdict (one decimal, floor), so a 19999/20000 TOTAL beside
    /// <c>FAIL: line 99.9% (min 100%)</c> reads <c>99.9%</c>, never <c>100.0%</c>. Passing
    /// dimensions and passing files keep normal rounding, so a passing gate renders
    /// byte-identical to <see cref="Format(CoverageReport, bool)"/>. Presentation only: the gate
    /// is not re-evaluated, and no verdict line is appended.
    /// </summary>
    public static string Format(CoverageReport report, GateResult gate, bool color) =>
        Render(report, color,
            lineFloorBelow: gate.LineBelowThreshold ? gate.MinLinePercent : null,
            branchFloorBelow: gate.BranchBelowThreshold ? gate.MinBranchPercent : null);

    /// <param name="lineFloorBelow">The missed line minimum when the gate failed lines, else null.</param>
    /// <param name="branchFloorBelow">The missed branch minimum when the gate failed branches, else null.</param>
    private static string Render(CoverageReport report, bool color, double? lineFloorBelow, double? branchFloorBelow)
    {
        var pen = new AnsiPen(color);
        var sb = new StringBuilder();
        var maxPath = Math.Max("File".Length, report.Files.Count > 0 ? report.Files.Max(static f => f.Path.Length) : 0);

        var headerPlain = $"{"File".PadRight(maxPath)}  {"Lines",10}  {"Line %",8}  {"Branches",10}  {"Branch %",8}";
        sb.AppendLine(pen.Bold(pen.Cyan(headerPlain)));
        sb.AppendLine(pen.Dim(new string('-', headerPlain.Length)));

        foreach (var f in report.Files.WorstFirst())
        {
            var lines = $"{f.LinesHit}/{f.LinesTotal}".PadLeft(10);
            var linePct = Pct(f.LineRate, Below(f.LineRate, lineFloorBelow));
            var branches = (f.HasBranchData ? $"{f.BranchesHit}/{f.BranchesTotal}" : "-").PadLeft(10);
            var branchPct = Pct(f.BranchRate, Below(f.BranchRate, branchFloorBelow));

            // Branch cells route through pen.Rate unconditionally: BranchRate is null exactly
            // when the file has no branch data, and a null rate falls to the pen's dim arm.
            sb.AppendLine(
                $"{f.Path.PadRight(maxPath)}  " +
                $"{pen.Rate(lines, f.LineRate)}  " +
                $"{pen.Rate(linePct, f.LineRate)}  " +
                $"{pen.Rate(branches, f.BranchRate)}  " +
                $"{pen.Rate(branchPct, f.BranchRate)}");
        }

        sb.AppendLine(pen.Dim(new string('-', headerPlain.Length)));

        var totalLines = $"{report.TotalLinesHit}/{report.TotalLines}".PadLeft(10);
        var totalLinePct = Pct(report.LineRate, floor: lineFloorBelow is not null);
        var totalBranches = (report.HasBranchData
            ? $"{report.TotalBranchesHit}/{report.TotalBranches}" : "-").PadLeft(10);
        var totalBranchPct = Pct(report.BranchRate, floor: branchFloorBelow is not null);

        sb.AppendLine(
            $"{pen.Bold("TOTAL".PadRight(maxPath))}  " +
            $"{pen.Bold(pen.Rate(totalLines, report.LineRate))}  " +
            $"{pen.Bold(pen.Rate(totalLinePct, report.LineRate))}  " +
            $"{pen.Bold(pen.Rate(totalBranches, report.BranchRate))}  " +
            $"{pen.Bold(pen.Rate(totalBranchPct, report.BranchRate))}");

        // One-line trailer; markdown owns the detailed list.
        if (report.Warnings.Count > 0)
            sb.AppendLine(pen.Dim($"Warnings: {report.Warnings.Count}"));

        return sb.ToString();
    }

    public static string FormatDiff(CoverageDiffResult diff) => FormatDiff(diff, color: false);

    /// <summary>
    /// The files whose coverage changed, the TOTAL row, and one line counting the unchanged files:
    /// a diff of two large reports reads as what moved, not as every file that stood still.
    /// </summary>
    public static string FormatDiff(CoverageDiffResult diff, bool color)
    {
        var pen = new AnsiPen(color);
        var sb = new StringBuilder();
        var changed = diff.Changed.ToList();
        // At least as wide as TOTAL, so the summary row aligns when every path is shorter or
        // nothing changed.
        var maxPath = changed.Select(static d => d.Path.Length).Append("TOTAL".Length).Max();

        var headerPlain = $"{"File".PadRight(maxPath)}  {"Before",8}  {"After",8}  {"Delta",8}  {"Change",10}";
        sb.AppendLine(pen.Bold(pen.Cyan(headerPlain)));
        sb.AppendLine(pen.Dim(new string('-', headerPlain.Length)));

        foreach (var d in changed)
        {
            var before = Pct(d.Before);
            var after = Pct(d.After);
            var change = $"{d.Change,10}";

            sb.AppendLine(
                $"{d.Path.PadRight(maxPath)}  " +
                $"{before}  " +
                $"{after}  " +
                $"{pen.Delta(DeltaCell(d.Delta), d.Delta)}  " +
                $"{ColorChange(pen, change, d.Change)}");
        }

        sb.AppendLine(pen.Dim(new string('-', headerPlain.Length)));
        sb.AppendLine(
            $"{pen.Bold("TOTAL".PadRight(maxPath))}  " +
            $"{pen.Bold(Pct(diff.BeforeRate))}  " +
            $"{pen.Bold(Pct(diff.AfterRate))}  " +
            $"{pen.Bold(pen.Delta(DeltaCell(diff.Delta), diff.Delta))}");
        sb.AppendLine(pen.Dim(Invariant($"Unchanged: {diff.UnchangedCount} {(diff.UnchangedCount == 1 ? "file" : "files")}")));

        // Codecov-style indirect-change summary: one line, only when there's anything to show.
        // Detailed per-file breakdown lives in the markdown formatter where it fits better.
        if (diff.TotalLineChanges > 0)
        {
            var affected = diff.WithLineChanges.Count();
            var lineWord = diff.TotalLineChanges == 1 ? "line" : "lines";
            var fileWord = affected == 1 ? "file" : "files";
            sb.AppendLine(pen.Dim(
                $"Indirect changes: {diff.TotalLineChanges} {lineWord} flipped across {affected} {fileWord}"));
        }

        return sb.ToString();
    }

    // The one fixed-width percent cell every rate column uses: right-aligned to 8 chars,
    // dash-padded when the rate is unmeasured (null), always invariant-formatted.
    // A failing cell floors through GateResult's one display policy (FloorToTenth).
    private static string Pct(double? rate, bool floor = false) =>
        rate is { } r ? Invariant($"{(floor ? GateResult.FloorToTenth(r * 100) : r * 100),7:F1}%") : "       -";

    // Same per-file rule as the markdown gate overload and the CLI offender list: only a file
    // genuinely below the missed minimum floors its cell.
    private static bool Below(double? rate, double? missedMinimum) =>
        missedMinimum is { } min && rate is { } r && !GateResult.MeetsThreshold(r, min);

    // The one delta cell policy, shared by per-file rows and the TOTAL row (and matching the
    // markdown formatter's sign convention): sign rendered as part of the number — '+' for
    // deltas >= 0, the value's own '-' otherwise — then right-aligned to 8 chars so the sign
    // can never detach from its digits. Null (unmeasured on either side) renders the dash cell.
    private static string DeltaCell(double? delta) =>
        (delta is { } d ? Invariant($"{(d >= 0 ? "+" : "")}{d * 100:F1}%") : "-").PadLeft(8);

    // A listed file was added, removed, or modified; unchanged files are only counted.
    private static string ColorChange(AnsiPen pen, string text, FileChangeKind kind) => kind switch
    {
        FileChangeKind.Added => pen.Green(text),
        FileChangeKind.Removed => pen.Red(text),
        _ => pen.Yellow(text)
    };
}

/// <summary>
/// The single worst-first file ordering policy shared by the human-facing text formatters
/// (table and markdown): unmeasured files (null line rate) sort first, then ascending line
/// rate; ties keep document order (stable sort). JSON deliberately keeps document order —
/// machines sort as they need — see the note in <see cref="JsonFormatter.Format"/>.
/// </summary>
internal static class FormatterOrdering
{
    internal static IOrderedEnumerable<FileCoverage> WorstFirst(this IReadOnlyList<FileCoverage> files) =>
        files.OrderBy(static f => f.LineRate ?? -1);
}
