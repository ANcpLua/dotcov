using System.Text;

namespace DotCov.Formatters;

public static class TableFormatter
{
    public static string Format(CoverageReport report) => Format(report, color: false);

    public static string Format(CoverageReport report, bool color)
    {
        var pen = new AnsiPen(color);
        var sb = new StringBuilder();
        var maxPath = Math.Max("File".Length, report.Files.Count > 0 ? report.Files.Max(f => f.Path.Length) : 0);

        var headerPlain = $"{"File".PadRight(maxPath)}  {"Lines",10}  {"Line %",8}  {"Branches",10}  {"Branch %",8}";
        sb.AppendLine(pen.Bold(pen.Cyan(headerPlain)));
        sb.AppendLine(pen.Dim(new string('-', headerPlain.Length)));

        foreach (var f in report.Files.OrderBy(f => f.LineRate))
        {
            var lines = $"{f.LinesHit}/{f.LinesTotal}".PadLeft(10);
            var linePct = $"{f.LineRate * 100,7:F1}%";
            var branches = (f.HasBranchData ? $"{f.BranchesHit}/{f.BranchesTotal}" : "-").PadLeft(10);
            var branchPct = (f.HasBranchData ? $"{f.BranchRate * 100,7:F1}%" : "       -").PadLeft(8);

            sb.AppendLine(
                $"{f.Path.PadRight(maxPath)}  " +
                $"{pen.Rate(lines, f.LineRate)}  " +
                $"{pen.Rate(linePct, f.LineRate)}  " +
                $"{(f.HasBranchData ? pen.Rate(branches, f.BranchRate) : pen.Dim(branches))}  " +
                $"{(f.HasBranchData ? pen.Rate(branchPct, f.BranchRate) : pen.Dim(branchPct))}");
        }

        sb.AppendLine(pen.Dim(new string('-', headerPlain.Length)));

        var totalLines = $"{report.TotalLinesHit}/{report.TotalLines}".PadLeft(10);
        var totalLinePct = $"{report.LineRate * 100,7:F1}%";
        var totalBranches = (report.HasBranchData
            ? $"{report.TotalBranchesHit}/{report.TotalBranches}" : "-").PadLeft(10);
        var totalBranchPct = (report.HasBranchData ? $"{report.BranchRate * 100,7:F1}%" : "       -").PadLeft(8);

        sb.AppendLine(
            $"{pen.Bold("TOTAL".PadRight(maxPath))}  " +
            $"{pen.Bold(pen.Rate(totalLines, report.LineRate))}  " +
            $"{pen.Bold(pen.Rate(totalLinePct, report.LineRate))}  " +
            $"{pen.Bold(report.HasBranchData ? pen.Rate(totalBranches, report.BranchRate) : pen.Dim(totalBranches))}  " +
            $"{pen.Bold(report.HasBranchData ? pen.Rate(totalBranchPct, report.BranchRate) : pen.Dim(totalBranchPct))}");

        return sb.ToString();
    }

    public static string FormatDiff(CoverageDiffResult diff) => FormatDiff(diff, color: false);

    public static string FormatDiff(CoverageDiffResult diff, bool color)
    {
        var pen = new AnsiPen(color);
        var sb = new StringBuilder();
        var maxPath = Math.Max("File".Length, diff.Files.Count > 0 ? diff.Files.Max(d => d.Path.Length) : 0);

        var headerPlain = $"{"File".PadRight(maxPath)}  {"Before",8}  {"After",8}  {"Delta",8}  {"Change",10}";
        sb.AppendLine(pen.Bold(pen.Cyan(headerPlain)));
        sb.AppendLine(pen.Dim(new string('-', headerPlain.Length)));

        foreach (var d in diff.Files)
        {
            var before = (d.Before.HasValue ? $"{d.Before.Value * 100:F1}%" : "-").PadLeft(8);
            var after = (d.After.HasValue ? $"{d.After.Value * 100:F1}%" : "-").PadLeft(8);
            var indicator = d.Delta switch { > 0 => "+", < 0 => "", _ => " " };
            var deltaText = $"{indicator}{d.Delta * 100,6:F1}%";
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
        var totalDeltaText = $"{sign}{diff.Delta * 100,6:F1}%";
        sb.AppendLine(
            $"{pen.Bold("TOTAL".PadRight(maxPath))}  " +
            $"{pen.Bold($"{diff.BeforeRate * 100,7:F1}%")}  " +
            $"{pen.Bold($"{diff.AfterRate * 100,7:F1}%")}  " +
            $"{pen.Bold(pen.Delta(totalDeltaText, diff.Delta))}");

        // Codecov-style indirect-change summary: one line, only when there's anything to show.
        // Detailed per-file breakdown lives in the markdown formatter where it fits better.
        if (diff.TotalLineChanges > 0)
        {
            var affected = diff.Files.Count(f => f.LineChanges.Count > 0);
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
