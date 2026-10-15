using System.Text;
using static System.FormattableString;

namespace DotCov.Formatters;

public static class MarkdownFormatter
{
    /// <summary>
    /// The report summary, worst file first. With <paramref name="top"/>, only that many file
    /// rows and a <c>… N more files</c> line; the totals still count every file.
    /// </summary>
    public static string Format(CoverageReport report, double? threshold = null, int? top = null)
    {
        // A gate that could not evaluate gets its own badge. Rendering NoData/Disabled as ✅
        // is what lets an unmeasured build read as a healthy one in a PR summary.
        var gate = threshold.HasValue ? report.Evaluate(threshold.Value) : (GateResult?)null;
        // The threshold exactly as evaluated (round-trip, like the gate overload): F0 would print
        // 79.6 as "80%" beside a ✅ for 79.8%, and 0.4 as "0%" beside a ❌.
        var thresholdLine = threshold.HasValue ? Invariant($"**Threshold:** {threshold.Value}%") : null;
        // Renders un-floored rates, an output shape this overload's consumers depend on. The
        // GateResult overload below is the floor-aware presentation.
        return Render(report, gate, thresholdLine, floorLine: false, floorBranch: false, top, offenders: null);
    }

    /// <summary>
    /// Render the report against an already-evaluated <see cref="GateResult"/> — the badge and
    /// verdict come from <paramref name="gate"/> with no re-evaluation, and any dimension the
    /// gate failed renders its rates floored (mirroring <see cref="GateResult.ToString"/>'s
    /// display policy), so the markdown body can never round a failing 79.96% up to the
    /// self-contradictory <c>80.0%</c> next to a <c>FAIL … 79.9%</c> verdict line. The
    /// backticked one-line <see cref="GateResult.ToString"/> verdict CI logs grep for closes
    /// the document — rendered here, not spliced on by callers, so badge, body, and verdict
    /// can never come from different evaluations. A failed gate lists what to fix next below the
    /// table: the first <paramref name="top"/> <see cref="CoverageReport.Offenders"/> with their
    /// rates, uncovered line ranges, and partially covered branch lines.
    /// </summary>
    public static string Format(CoverageReport report, GateResult gate, int top = CoverageReport.DefaultOffenderLimit) =>
        Render(report, gate,
            Invariant($"**Threshold:** line {gate.MinLinePercent}%, branch {gate.MinBranchPercent}%"),
            // Floor only the dimension(s) that actually fell short: flooring a passing rate
            // would misreport it (2499/2500 must stay 100.0%, not become 99.9%).
            floorLine: gate.LineBelowThreshold, floorBranch: gate.BranchBelowThreshold, top: null, offenders: (gate, top))
        + Invariant($"{Environment.NewLine}`{gate}`{Environment.NewLine}");

    /// <param name="report">The measured report to render.</param>
    /// <param name="gate">The evaluated verdict, or null when no threshold was supplied.</param>
    /// <param name="thresholdLine">The threshold description, or null to omit it.</param>
    /// <param name="floorLine">Whether to floor line rates in the failed dimension.</param>
    /// <param name="floorBranch">Whether to floor branch rates in the failed dimension.</param>
    /// <param name="top">The number of file rows shown, or null for all of them.</param>
    /// <param name="offenders">The gate whose offenders are listed and how many, or null for no list.</param>
    private static string Render(
        CoverageReport report, GateResult? gate, string? thresholdLine, bool floorLine, bool floorBranch,
        int? top, (GateResult Gate, int Top)? offenders)
    {
        var sb = new StringBuilder();

        sb.AppendLine($"## Coverage Report{Badge(gate)}");
        sb.AppendLine();
        AppendSummary(sb, report, gate, thresholdLine, floorLine, floorBranch);

        sb.AppendLine();
        sb.AppendLine("| File | Lines | Line % | Branches | Branch % |");
        sb.AppendLine("|------|------:|-------:|---------:|---------:|");

        IEnumerable<FileCoverage> rows = report.Files.WorstFirst();
        if (top is { } shown) rows = rows.Take(shown);
        foreach (var f in rows)
            sb.AppendLine(FileRow(f, gate, floorLine, floorBranch));

        // A blank line ends the table; without it GFM reads the next line as one more row.
        if (top is { } limit && report.Files.Count > limit)
        {
            sb.AppendLine();
            sb.AppendLine(Invariant($"_… {report.Files.Count - limit} more files_"));
        }

        if (offenders is { } list)
            AppendOffenders(sb, report, list.Gate, list.Top);

        AppendWarnings(sb, report);

        return sb.ToString();
    }

    private static void AppendOffenders(StringBuilder sb, CoverageReport report, GateResult gate, int top)
    {
        var offenders = report.Offenders(gate);
        if (offenders.Count is 0) return;

        sb.AppendLine();
        sb.AppendLine($"### Files below {OffenderText.Dimensions(gate)} threshold");
        sb.AppendLine();
        foreach (var f in offenders.Take(top))
        {
            var detail = OffenderText.Detail(f);
            sb.AppendLine($"- {Code(f.Path)} — {OffenderText.Rates(f, gate, ", ")}{(detail.Length > 0 ? $" — {detail}" : "")}");
        }

        // A blank line ends the list; without it the line continues the last item.
        if (offenders.Count > top)
        {
            sb.AppendLine();
            sb.AppendLine(Invariant($"_… {offenders.Count - top} more files_"));
        }
    }

    private static string Badge(GateResult? gate) => gate?.Outcome switch
    {
        GateOutcome.Pass => " ✅",
        GateOutcome.Fail => " ❌",
        GateOutcome.NoData => " ⚠️",
        GateOutcome.Disabled => " ⚠️",
        _ => "",
    };

    private static void AppendSummary(
        StringBuilder sb, CoverageReport report, GateResult? gate, string? thresholdLine, bool floorLine, bool floorBranch)
    {
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
    }

    private static string FileRow(FileCoverage f, GateResult? gate, bool floorLine, bool floorBranch)
    {
        // Per-file flooring follows the same rule as the CLI offender list: only files
        // genuinely below the missed minimum in a failing dimension floor their display.
        var floorFileLine = floorLine && gate is { } lg
            && f.LineRate is { } fileLr && !GateResult.MeetsThreshold(fileLr, lg.MinLinePercent);
        var floorFileBranch = floorBranch && gate is { } bg
            && f.BranchRate is { } fileBr && !GateResult.MeetsThreshold(fileBr, bg.MinBranchPercent);
        var branches = f.BranchesTotal > 0 ? $"{f.BranchesHit}/{f.BranchesTotal}" : "-";
        return $"| {TableCode(f.Path)} | {f.LinesHit}/{f.LinesTotal} | {Pct(f.LineRate, floorFileLine)} | {branches} | {Pct(f.BranchRate, floorFileBranch)} |";
    }

    private static void AppendWarnings(StringBuilder sb, CoverageReport report)
    {
        if (report.Warnings.Count is 0) return;

        sb.AppendLine();
        sb.AppendLine("### Warnings");
        sb.AppendLine();
        foreach (var w in report.Warnings)
            sb.AppendLine($"- {Code($"{w.File}:{w.Line}")} — {w.Kind}: {w.Detail}");
    }

    public static string FormatDiff(CoverageDiffResult diff)
    {
        var sb = new StringBuilder();

        // Same movement threshold as the change kind and AnsiPen.Delta: a sub-epsilon wobble
        // is Unchanged and painted dim, so the header must not call it a rise or a fall.
        var icon = diff.Delta switch
        {
            >= CoverageDiff.MovementEpsilon => "📈",
            <= -CoverageDiff.MovementEpsilon => "📉",
            _ => "➡️"
        };
        sb.AppendLine($"## Coverage Diff {icon}");
        sb.AppendLine();
        // Null rates render as "-" like the table/JSON formatters: a diff against an
        // empty report is not a comparison, so no percentage may be asserted for it.
        sb.AppendLine($"**Overall:** {Pct(diff.BeforeRate)} → {Pct(diff.AfterRate)} ({SignedPct(diff.Delta)})");
        sb.AppendLine();
        sb.AppendLine("| File | Before | After | Delta | Change |");
        sb.AppendLine("|------|-------:|------:|------:|--------|");

        // Only what moved; the files that stood still are counted, the table's twin of the
        // console diff.
        foreach (var d in diff.Changed)
            sb.AppendLine($"| {TableCode(d.Path)} | {Pct(d.Before)} | {Pct(d.After)} | {SignedPct(d.Delta)} | {d.Change} |");

        sb.AppendLine();
        sb.AppendLine(Invariant($"**Unchanged:** {diff.UnchangedCount} {(diff.UnchangedCount == 1 ? "file" : "files")}"));

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
        if (floorTowardFail) pct = GateResult.FloorToTenth(pct);
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

            sb.AppendLine($"- {Code(d.Path)}: {string.Join(", ", fragments)}");
        }
    }

    /// <summary>
    /// <paramref name="text"/> as one inline code span, whatever it contains: line breaks
    /// become spaces, and a text carrying backticks gets a fence one backtick longer than its
    /// longest backtick run, padded with the single space CommonMark strips — a lone-backtick
    /// fence would close at the first backtick of the text and spill the rest as prose.
    /// </summary>
    internal static string Code(string text)
    {
        text = text.ReplaceLineEndings(" ");
        int longest = 0, run = 0;
        foreach (var c in text)
            longest = Math.Max(longest, run = c is '`' ? run + 1 : 0);
        if (longest is 0) return $"`{text}`";

        var fence = new string('`', longest + 1);
        return $"{fence} {text} {fence}";
    }

    /// <summary>
    /// <see cref="Code"/> for a table cell: GFM splits cells on every unescaped <c>|</c>, code
    /// spans included, so a raw pipe in a name would shift every later cell one column right.
    /// The table parser consumes the <c>\|</c> escape before inline parsing, so it renders as
    /// <c>|</c> inside the span.
    /// </summary>
    internal static string TableCode(string text) => Code(text.Replace("|", "\\|", StringComparison.Ordinal));
}
