using System.Text;

namespace DotCov.Formatters;

public static class TableFormatter
{
    public static string Format(CoverageReport report) => Format(report, color: false);

    public static string Format(CoverageReport report, bool color)
    {
        var pen = new AnsiPen(color);
        var sb = new StringBuilder();
        var maxPath = Math.Max("File".Length, report.Files.Count > 0 ? report.Files.Max(static f => f.Path.Length) : 0);

        var headerPlain = $"{"File".PadRight(maxPath)}  {"Lines",10}  {"Line %",8}  {"Branches",10}  {"Branch %",8}";
        sb.AppendLine(pen.Bold(pen.Cyan(headerPlain)));
        sb.AppendLine(pen.Dim(new string('-', headerPlain.Length)));

        foreach (var f in report.Files.OrderBy(static f => f.LineRate ?? -1))
        {
            var lines = $"{f.LinesHit}/{f.LinesTotal}".PadLeft(10);
            var linePct = (f.LineRate is { } r ? $"{r * 100,7:F1}%" : "       -").PadLeft(8);
            var branches = (f.HasBranchData ? $"{f.BranchesHit}/{f.BranchesTotal}" : "-").PadLeft(10);
            var branchPct = (f.BranchRate is { } br ? $"{br * 100,7:F1}%" : "       -").PadLeft(8);

            sb.AppendLine(
                $"{f.Path.PadRight(maxPath)}  " +
                $"{pen.Rate(lines, f.LineRate)}  " +
                $"{pen.Rate(linePct, f.LineRate)}  " +
                $"{(f.HasBranchData ? pen.Rate(branches, f.BranchRate) : pen.Dim(branches))}  " +
                $"{(f.HasBranchData ? pen.Rate(branchPct, f.BranchRate) : pen.Dim(branchPct))}");
        }

        sb.AppendLine(pen.Dim(new string('-', headerPlain.Length)));

        var totalLines = $"{report.TotalLinesHit}/{report.TotalLines}".PadLeft(10);
        var totalLinePct = (report.LineRate is { } tr ? $"{tr * 100,7:F1}%" : "       -").PadLeft(8);
        var totalBranches = (report.HasBranchData
            ? $"{report.TotalBranchesHit}/{report.TotalBranches}" : "-").PadLeft(10);
        var totalBranchPct = (report.BranchRate is { } tbr ? $"{tbr * 100,7:F1}%" : "       -").PadLeft(8);

        sb.AppendLine(
            $"{pen.Bold("TOTAL".PadRight(maxPath))}  " +
            $"{pen.Bold(pen.Rate(totalLines, report.LineRate))}  " +
            $"{pen.Bold(pen.Rate(totalLinePct, report.LineRate))}  " +
            $"{pen.Bold(report.HasBranchData ? pen.Rate(totalBranches, report.BranchRate) : pen.Dim(totalBranches))}  " +
            $"{pen.Bold(report.HasBranchData ? pen.Rate(totalBranchPct, report.BranchRate) : pen.Dim(totalBranchPct))}");

        // One-line trailer; markdown owns the detailed list. Stays silent when nothing
        // is wrong so existing CLI users see no visual change for clean reports.
        if (report.Warnings.Count > 0)
            sb.AppendLine(pen.Dim($"Warnings: {report.Warnings.Count}"));

        return sb.ToString();
    }

    public static string FormatDiff(CoverageDiffResult diff) => FormatDiff(diff, color: false);

    public static string FormatDiff(CoverageDiffResult diff, bool color)
    {
        var pen = new AnsiPen(color);
        var sb = new StringBuilder();
        var maxPath = Math.Max("File".Length, diff.Files.Count > 0 ? diff.Files.Max(static d => d.Path.Length) : 0);

        var headerPlain = $"{"File".PadRight(maxPath)}  {"Before",8}  {"After",8}  {"Delta",8}  {"Change",10}";
        sb.AppendLine(pen.Bold(pen.Cyan(headerPlain)));
        sb.AppendLine(pen.Dim(new string('-', headerPlain.Length)));

        foreach (var d in diff.Files)
        {
            var before = (d.Before.HasValue ? $"{d.Before.Value * 100:F1}%" : "-").PadLeft(8);
            var after = (d.After.HasValue ? $"{d.After.Value * 100:F1}%" : "-").PadLeft(8);
            var indicator = d.Delta switch { > 0 => "+", < 0 => "", _ => " " };
            var deltaText = d.Delta is { } dv ? $"{indicator}{dv * 100,6:F1}%" : "       -";
            var change = $"{d.Change,10}";

            sb.AppendLine(
                $"{d.Path.PadRight(maxPath)}  " +
                $"{before}  " +
                $"{after}  " +
                $"{pen.Delta(deltaText, d.Delta)}  " +
                $"{ColorChange(pen, change, d.Change)}");
        }

        sb.AppendLine(pen.Dim(new string('-', headerPlain.Length)));
        var sign = diff.Delta >= 0 ? "+" : "";
        var totalDeltaText = diff.Delta is { } td ? $"{sign}{td * 100,6:F1}%" : "       -";
        sb.AppendLine(
            $"{pen.Bold("TOTAL".PadRight(maxPath))}  " +
            $"{pen.Bold(diff.BeforeRate is { } bt ? $"{bt * 100,7:F1}%" : "       -")}  " +
            $"{pen.Bold(diff.AfterRate is { } at ? $"{at * 100,7:F1}%" : "       -")}  " +
            $"{pen.Bold(pen.Delta(totalDeltaText, diff.Delta))}");

        // Codecov-style indirect-change summary: one line, only when there's anything to show.
        // Detailed per-file breakdown lives in the markdown formatter where it fits better.
        if (diff.TotalLineChanges > 0)
        {
            var affected = diff.Files.Count(static f => f.LineChanges.Count > 0);
            var lineWord = diff.TotalLineChanges == 1 ? "line" : "lines";
            var fileWord = affected == 1 ? "file" : "files";
            sb.AppendLine(pen.Dim(
                $"Indirect changes: {diff.TotalLineChanges} {lineWord} flipped across {affected} {fileWord}"));
        }

        return sb.ToString();
    }

    private static string ColorChange(AnsiPen pen, string text, FileChangeKind kind) => kind switch
    {
        FileChangeKind.Added => pen.Green(text),
        FileChangeKind.Removed => pen.Red(text),
        FileChangeKind.Modified => pen.Yellow(text),
        _ => pen.Dim(text)
    };
}
