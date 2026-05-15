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

        sb.AppendLine();
        sb.AppendLine($"### Indirect changes ({diff.TotalLineChanges} lines across {affected.Count} files)");
        sb.AppendLine();

        foreach (var d in affected)
        {
            var newlyHit = d.LineChanges.Count(c => c.Change is LineChangeKind.NewlyHit);
            var newlyMissed = d.LineChanges.Count(c => c.Change is LineChangeKind.NewlyMissed);
            var added = d.LineChanges.Count(c => c.Change is LineChangeKind.Added);
            var removed = d.LineChanges.Count(c => c.Change is LineChangeKind.Removed);

            var fragments = new List<string>(4);
            if (newlyMissed > 0) fragments.Add($"{newlyMissed} newly missed");
            if (newlyHit > 0) fragments.Add($"{newlyHit} newly hit");
            if (added > 0) fragments.Add($"{added} added");
            if (removed > 0) fragments.Add($"{removed} removed");

            sb.AppendLine($"- `{d.Path}`: {string.Join(", ", fragments)}");
        }
    }
}
