using System.Text;

namespace DotCov.Formatters;

public static class TableFormatter
{
    public static string Format(CoverageReport report)
    {
        var sb = new StringBuilder();
        var maxPath = Math.Max("File".Length, report.Files.Count > 0 ? report.Files.Max(f => f.Path.Length) : 0);

        var header = $"{"File".PadRight(maxPath)}  {"Lines",10}  {"Line %",8}  {"Branches",10}  {"Branch %",8}";
        sb.AppendLine(header);
        sb.AppendLine(new string('-', header.Length));

        foreach (var f in report.Files.OrderBy(f => f.LineRate))
        {
            var branches = f.BranchesTotal > 0 ? $"{f.BranchesHit}/{f.BranchesTotal}" : "-";
            sb.AppendLine(
                $"{f.Path.PadRight(maxPath)}  {$"{f.LinesHit}/{f.LinesTotal}",10}  {f.LineRate * 100,7:F1}%  {branches,10}  {f.BranchRate * 100,7:F1}%");
        }

        sb.AppendLine(new string('-', header.Length));

        var totalBranches = report.TotalBranches > 0
            ? $"{report.TotalBranchesHit}/{report.TotalBranches}" : "-";
        sb.AppendLine(
            $"{"TOTAL".PadRight(maxPath)}  {$"{report.TotalLinesHit}/{report.TotalLines}",10}  {report.LineRate * 100,7:F1}%  {totalBranches,10}  {report.BranchRate * 100,7:F1}%");

        return sb.ToString();
    }

    public static string FormatDiff(CoverageDiffResult diff)
    {
        var sb = new StringBuilder();
        var maxPath = Math.Max("File".Length, diff.Files.Count > 0 ? diff.Files.Max(d => d.Path.Length) : 0);

        var header = $"{"File".PadRight(maxPath)}  {"Before",8}  {"After",8}  {"Delta",8}  {"Change",10}";
        sb.AppendLine(header);
        sb.AppendLine(new string('-', header.Length));

        foreach (var d in diff.Files)
        {
            var before = d.Before.HasValue ? $"{d.Before.Value * 100:F1}%" : "-";
            var after = d.After.HasValue ? $"{d.After.Value * 100:F1}%" : "-";
            var indicator = d.Delta switch { > 0 => "+", < 0 => "", _ => " " };
            sb.AppendLine(
                $"{d.Path.PadRight(maxPath)}  {before,8}  {after,8}  {indicator}{d.Delta * 100,6:F1}%  {d.Change,10}");
        }

        sb.AppendLine(new string('-', header.Length));
        var sign = diff.Delta >= 0 ? "+" : "";
        sb.AppendLine(
            $"{"TOTAL".PadRight(maxPath)}  {diff.BeforeRate * 100,7:F1}%  {diff.AfterRate * 100,7:F1}%  {sign}{diff.Delta * 100,6:F1}%");

        return sb.ToString();
    }
}
