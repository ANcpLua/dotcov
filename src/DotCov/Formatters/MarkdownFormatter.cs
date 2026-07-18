using System.Text;

namespace DotCov.Formatters;

public static class MarkdownFormatter
{
    public static string Format(CoverageReport report, double? threshold = null)
    {
        var sb = new StringBuilder();
        // A gate that could not evaluate gets its own badge. Rendering NoData/Disabled as ✅
        // is what lets an unmeasured build read as a healthy one in a PR summary.
        var gate = threshold.HasValue ? report.Evaluate(threshold.Value) : (GateResult?)null;
        var badge = gate?.Outcome switch
        {
            GateOutcome.Pass => " ✅",
            GateOutcome.Fail => " ❌",
            GateOutcome.NoData => " ⚠️",
            GateOutcome.Disabled => " ⚠️",
            _ => "",
        };

        sb.AppendLine($"## Coverage Report{badge}");
        sb.AppendLine();
        sb.AppendLine(report.LineRate is { } lr
            ? $"**Line coverage:** {lr * 100:F1}% ({report.TotalLinesHit}/{report.TotalLines})"
            : "**Line coverage:** no data - the report contains no measured lines");
        if (gate is { IsInconclusive: true } g)
        {
            sb.AppendLine();
            sb.AppendLine($"> **No verdict:** {g.Reason}.");
        }

        sb.AppendLine(report.HasBranchData
            ? $"**Branch coverage:** {report.BranchRate!.Value * 100:F1}% ({report.TotalBranchesHit}/{report.TotalBranches})"
            : "**Branch coverage:** _no branch data emitted_");

        if (threshold.HasValue)
            sb.AppendLine($"**Threshold:** {threshold.Value:F0}%");

        sb.AppendLine();
        sb.AppendLine("| File | Lines | Line % | Branches | Branch % |");
        sb.AppendLine("|------|------:|-------:|---------:|---------:|");

        foreach (var f in report.Files.OrderBy(static f => f.LineRate ?? -1))
        {
            var branches = f.BranchesTotal > 0 ? $"{f.BranchesHit}/{f.BranchesTotal}" : "-";
            var branchPct = f.BranchRate is { } b ? $"{b * 100:F1}%" : "-";
            sb.AppendLine(
                $"| `{f.Path}` | {f.LinesHit}/{f.LinesTotal} | {(f.LineRate is { } r ? $"{r * 100:F1}%" : "-")} | {branches} | {branchPct} |");
        }

        AppendWarnings(sb, report);

        return sb.ToString();
    }

    private static void AppendWarnings(StringBuilder sb, CoverageReport report)
    {
        // Structured anomaly surface — kept additive so existing consumers see no change
        // when nothing diverged. Detailed list lives here because table/JSON have their
        // own conventions; markdown is the natural place for full per-entry context.
        if (report.Warnings.Count is 0) return;

        sb.AppendLine();
        sb.AppendLine("### Warnings");
        sb.AppendLine();
        foreach (var w in report.Warnings)
            sb.AppendLine($"- `{w.File}:{w.Line}` — {w.Kind}: {w.Detail}");
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
            // Visitor dispatch over the closed LineDelta hierarchy. Switch is the
            // compile-time-exhaustive sibling of Match<T> — adding a fifth variant breaks
            // this signature so no fallback arm can ever silently miscount. Counter order
            // matches the rendered output: newly missed → newly hit → added → removed.
            int newlyMissed = 0, newlyHit = 0, added = 0, removed = 0;
            foreach (var c in d.LineChanges)
                c.Switch(
                    added:       _ => added++,
                    removed:     _ => removed++,
                    newlyHit:    _ => newlyHit++,
                    newlyMissed: _ => newlyMissed++);

            var fragments = new List<string>(4);
            if (newlyMissed > 0) fragments.Add($"{newlyMissed} newly missed");
            if (newlyHit    > 0) fragments.Add($"{newlyHit} newly hit");
            if (added       > 0) fragments.Add($"{added} added");
            if (removed     > 0) fragments.Add($"{removed} removed");

            sb.AppendLine($"- `{d.Path}`: {string.Join(", ", fragments)}");
        }
    }
}
