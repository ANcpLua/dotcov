using System.Text;
using static System.FormattableString;

namespace DotCov.Formatters;

public static class MarkdownFormatter
{
    public static string Format(CoverageReport report, double? threshold = null)
    {
        // A gate that could not evaluate gets its own badge. Rendering NoData/Disabled as ✅
        // is what lets an unmeasured build read as a healthy one in a PR summary.
        var gate = threshold.HasValue ? report.Evaluate(threshold.Value) : (GateResult?)null;
        var thresholdLine = threshold.HasValue ? Invariant($"**Threshold:** {threshold.Value:F0}%") : null;
        // Legacy path renders un-floored rates — its output shape is pinned by existing
        // consumers. The GateResult overload below is the floor-aware presentation.
        return Render(report, gate, thresholdLine, floorFailing: false);
    }

    /// <summary>
    /// Render the report against an already-evaluated <see cref="GateResult"/> — the badge and
    /// verdict come from <paramref name="gate"/> with no re-evaluation, and any dimension the
    /// gate failed renders its rates floored (mirroring <see cref="GateResult.ToString"/>'s
    /// display policy), so the markdown body can never round a failing 79.96% up to the
    /// self-contradictory <c>80.0%</c> next to a <c>FAIL … 79.9%</c> verdict line. The
    /// backticked one-line <see cref="GateResult.ToString"/> verdict CI logs grep for closes
    /// the document — rendered here, not spliced on by callers, so badge, body, and verdict
    /// can never come from different evaluations.
    /// </summary>
    public static string Format(CoverageReport report, GateResult gate) =>
        Render(report, gate,
            Invariant($"**Threshold:** line {gate.MinLinePercent}%, branch {gate.MinBranchPercent}%"),
            floorFailing: true)
        + Invariant($"{Environment.NewLine}`{gate}`{Environment.NewLine}");

    private static string Render(CoverageReport report, GateResult? gate, string? thresholdLine, bool floorFailing)
    {
        var sb = new StringBuilder();
        var badge = gate?.Outcome switch
        {
            GateOutcome.Pass => " ✅",
            GateOutcome.Fail => " ❌",
            GateOutcome.NoData => " ⚠️",
            GateOutcome.Disabled => " ⚠️",
            _ => "",
        };

        // Floor only the dimension(s) that actually fell short: flooring a passing rate would
        // misreport it (2499/2500 must stay 100.0%, not become 99.9%).
        var floorLine = floorFailing && gate is { LineBelowThreshold: true };
        var floorBranch = floorFailing && gate is { BranchBelowThreshold: true };

        sb.AppendLine($"## Coverage Report{badge}");
        sb.AppendLine();
        sb.AppendLine(report.LineRate is { } lr
            ? Invariant($"**Line coverage:** {Percent(lr, floorLine)} ({report.TotalLinesHit}/{report.TotalLines})")
            : "**Line coverage:** no data - the report contains no measured lines");
        if (gate is { IsInconclusive: true } g)
        {
            sb.AppendLine();
            sb.AppendLine($"> **No verdict:** {g.Reason}.");
            // Blank line closes the blockquote: without it, CommonMark lazy continuation
            // pulls the branch-coverage and threshold lines into the quote in every
            // rendered NoData/Disabled step summary.
            sb.AppendLine();
        }

        sb.AppendLine(report.HasBranchData
            ? Invariant($"**Branch coverage:** {Percent(report.BranchRate!.Value, floorBranch)} ({report.TotalBranchesHit}/{report.TotalBranches})")
            : "**Branch coverage:** _no branch data emitted_");

        if (thresholdLine is not null)
            sb.AppendLine(thresholdLine);

        sb.AppendLine();
        sb.AppendLine("| File | Lines | Line % | Branches | Branch % |");
        sb.AppendLine("|------|------:|-------:|---------:|---------:|");

        foreach (var f in report.Files.WorstFirst())
        {
            // Per-file flooring follows the same rule as the CLI offender list: only files
            // genuinely below the missed minimum in a failing dimension floor their display.
            var floorFileLine = floorLine && gate is { } lg
                && f.LineRate is { } fileLr && !GateResult.MeetsThreshold(fileLr, lg.MinLinePercent);
            var floorFileBranch = floorBranch && gate is { } bg
                && f.BranchRate is { } fileBr && !GateResult.MeetsThreshold(fileBr, bg.MinBranchPercent);
            var branches = f.BranchesTotal > 0 ? $"{f.BranchesHit}/{f.BranchesTotal}" : "-";
            sb.AppendLine(
                $"| `{f.Path}` | {f.LinesHit}/{f.LinesTotal} | {Pct(f.LineRate, floorFileLine)} | {branches} | {Pct(f.BranchRate, floorFileBranch)} |");
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
        // Null rates render as "-" like the table/JSON formatters: a diff against an
        // empty report is not a comparison, so no percentage may be asserted for it.
        sb.AppendLine($"**Overall:** {Pct(diff.BeforeRate)} → {Pct(diff.AfterRate)} ({SignedPct(diff.Delta)})");
        sb.AppendLine();
        sb.AppendLine("| File | Before | After | Delta | Change |");
        sb.AppendLine("|------|-------:|------:|------:|--------|");

        foreach (var d in diff.Files)
            sb.AppendLine($"| `{d.Path}` | {Pct(d.Before)} | {Pct(d.After)} | {SignedPct(d.Delta)} | {d.Change} |");

        AppendIndirectChanges(sb, diff);

        return sb.ToString();
    }

    private static string Pct(double? rate, bool floorTowardFail = false) =>
        rate is { } r ? Percent(r, floorTowardFail) : "-";

    // Mirrors GateResult.ToString's display policy through the same internal epsilon: a
    // failing rate floors at one decimal (79.96% renders 79.9%, never a rounded-up 80.0%
    // that reads as equal to the minimum it missed), while float noise a hair under a
    // tenth boundary is absorbed rather than floored an extra tenth down.
    private static string Percent(double rate, bool floorTowardFail)
    {
        var pct = rate * 100;
        if (floorTowardFail) pct = Math.Floor(pct * 10 + GateResult.RateEpsilon) / 10;
        return Invariant($"{pct:F1}%");
    }

    private static string SignedPct(double? delta) =>
        delta is { } d ? Invariant($"{(d >= 0 ? "+" : "")}{d * 100:F1}%") : "-";

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
