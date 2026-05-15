using System.Text;

namespace DotCov.Formatters;

public static class MarkdownFormatter
{
    public static string Format(CoverageReport report, double? threshold = null)
    {
        var sb = new StringBuilder();
        var status = threshold.HasValue
            ? report.MeetsThreshold(threshold.Value) ? "pass" : "fail"
            : null;

        sb.AppendLine($"## Coverage Report{(status is "pass" ? " ✅" : status is "fail" ? " ❌" : "")}");
        sb.AppendLine();
        sb.AppendLine($"**Line coverage:** {report.LineRate * 100:F1}% ({report.TotalLinesHit}/{report.TotalLines})");
        sb.AppendLine(report.HasBranchData
            ? $"**Branch coverage:** {report.BranchRate * 100:F1}% ({report.TotalBranchesHit}/{report.TotalBranches})"
            : "**Branch coverage:** _no branch data emitted_");

        if (threshold.HasValue)
            sb.AppendLine($"**Threshold:** {threshold.Value:F0}%");

        sb.AppendLine();
        sb.AppendLine("| File | Lines | Line % | Branches | Branch % |");
        sb.AppendLine("|------|------:|-------:|---------:|---------:|");

        foreach (var f in report.Files.OrderBy(f => f.LineRate))
        {
            var branches = f.BranchesTotal > 0 ? $"{f.BranchesHit}/{f.BranchesTotal}" : "-";
            var branchPct = f.BranchesTotal > 0 ? $"{f.BranchRate * 100:F1}%" : "-";
            sb.AppendLine(
                $"| `{f.Path}` | {f.LinesHit}/{f.LinesTotal} | {f.LineRate * 100:F1}% | {branches} | {branchPct} |");
        }

        return sb.ToString();
    }

    public static string FormatDiff(CoverageDiffResult diff)
    {
        var sb = new StringBuilder();

        var icon = diff.Delta switch { > 0 => "📈", < 0 => "📉", _ => "➡️" };
        sb.AppendLine($"## Coverage Diff {icon}");
        sb.AppendLine();
        sb.AppendLine(
            $"**Overall:** {diff.BeforeRate * 100:F1}% → {diff.AfterRate * 100:F1}% ({(diff.Delta >= 0 ? "+" : "")}{diff.Delta * 100:F1}%)");
        sb.AppendLine();
        sb.AppendLine("| File | Before | After | Delta | Change |");
        sb.AppendLine("|------|-------:|------:|------:|--------|");

        foreach (var d in diff.Files)
        {
            var before = d.Before.HasValue ? $"{d.Before.Value * 100:F1}%" : "-";
            var after = d.After.HasValue ? $"{d.After.Value * 100:F1}%" : "-";
            var sign = d.Delta >= 0 ? "+" : "";
            sb.AppendLine($"| `{d.Path}` | {before} | {after} | {sign}{d.Delta * 100:F1}% | {d.Change} |");
        }

        AppendIndirectChanges(sb, diff);

        return sb.ToString();
    }

    private static void AppendIndirectChanges(StringBuilder sb, CoverageDiffResult diff)
    {
        // Codecov-style "indirect changes" surface: lines whose hit/miss state flipped
        // inside files that exist on both sides of the diff. Most often signals removed
        // tests, dependency upgrades that change execution paths, or upstream regressions.
        var affected = diff.WithLineChanges.ToList();
        if (affected.Count is 0) return;

        var lineWord = diff.TotalLineChanges == 1 ? "line" : "lines";
        var fileWord = affected.Count == 1 ? "file" : "files";
        sb.AppendLine();
        sb.AppendLine($"### Indirect changes ({diff.TotalLineChanges} {lineWord} across {affected.Count} {fileWord})");
        sb.AppendLine();

        foreach (var d in affected)
        {
            // Type-pattern chain over the closed LineDelta hierarchy. LineDelta's ctor is
            // private so the four nested sealed records are the only possible runtime
            // types; the final unguarded cast surfaces a new variant as an
            // InvalidCastException instead of silently miscounting. Order matches the
            // rendered output: newly missed → newly hit → added → removed.
            var counts = (newlyMissed: 0, newlyHit: 0, added: 0, removed: 0);
            foreach (var c in d.LineChanges)
            {
                if (c is LineDelta.NewlyMissed) counts.newlyMissed++;
                else if (c is LineDelta.NewlyHit) counts.newlyHit++;
                else if (c is LineDelta.Added) counts.added++;
                else { _ = (LineDelta.Removed)c; counts.removed++; }
            }

            var fragments = new List<string>(4);
            if (counts.newlyMissed > 0) fragments.Add($"{counts.newlyMissed} newly missed");
            if (counts.newlyHit    > 0) fragments.Add($"{counts.newlyHit} newly hit");
            if (counts.added       > 0) fragments.Add($"{counts.added} added");
            if (counts.removed     > 0) fragments.Add($"{counts.removed} removed");

            sb.AppendLine($"- `{d.Path}`: {string.Join(", ", fragments)}");
        }
    }
}
