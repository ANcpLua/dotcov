namespace DotCov;

public readonly record struct BranchDetail(int Line, int Covered, int Total);

/// <summary>
/// Codecov-style three-state line classification. A line that was executed but whose
/// conditional branches were not all exercised is <see cref="Partial"/> — distinct from
/// <see cref="Hit"/> (fully exercised) and <see cref="Miss"/> (not executed at all).
/// Used for the strict coverage view; the canonical <see cref="FileCoverage.LineRate"/>
/// keeps treating partials as hit to stay aligned with the Cobertura source-of-truth.
/// </summary>
public enum LineStatus { Miss, Partial, Hit }

/// <summary>
/// Kind of anomaly observed by the parser or merger that would otherwise be swallowed
/// silently. Surfacing these via <see cref="CoverageReport.Warnings"/> lets consumers
/// (CI gates, observability sinks, AI test generators) react to malformed inputs and
/// divergent multi-job uploads without the library having to throw.
/// </summary>
public enum CoverageWarningKind
{
    /// <summary>
    /// Two reports disagreed on the branch <c>Total</c> for the same source line (e.g.
    /// one CI job built Release, the other Debug). <see cref="FileCoverage.MergeWith"/>
    /// keeps the larger total via <c>Math.Max</c>; this warning records the divergence.
    /// </summary>
    BranchTotalMismatch,

    /// <summary>
    /// A Cobertura <c>&lt;line&gt;</c> carried <c>branch="true"</c> but its
    /// <c>condition-coverage</c> attribute didn't match the <c>(covered/total)</c>
    /// shape, or the numbers overflowed <see cref="int"/>. The parser drops the branch
    /// entry; this warning makes the omission observable so emitter regressions
    /// (e.g. malformed Coverlet output) don't silently zero out branch coverage.
    /// </summary>
    MalformedConditionCoverage
}

/// <summary>
/// Structured anomaly record surfaced via <see cref="CoverageReport.Warnings"/>.
/// </summary>
/// <param name="Kind">Anomaly classification — see <see cref="CoverageWarningKind"/>.</param>
/// <param name="File">Cobertura <c>filename</c> the anomaly applies to.</param>
/// <param name="Line">Source line the anomaly applies to. Zero when the anomaly is file-scoped.</param>
/// <param name="Detail">Human-readable context: divergent values, the raw malformed string, etc.</param>
public readonly record struct CoverageWarning(
    CoverageWarningKind Kind,
    string File,
    int Line,
    string Detail);

public readonly record struct FileCoverage(
    string Path,
    int LinesHit,
    int LinesTotal,
    int BranchesHit,
    int BranchesTotal)
{
    /// <summary>
    /// Ratio of hit lines, or <c>null</c> when the file carries no line data. Null is not zero
    /// and not one: it means the question is unanswerable, and a caller that formats or compares
    /// it must say so rather than invent a number.
    /// </summary>
    public double? LineRate => LinesTotal is 0 ? null : (double)LinesHit / LinesTotal;

    /// <summary>Ratio of hit branches, or <c>null</c> when the file carries no branch data.</summary>
    public double? BranchRate => BranchesTotal is 0 ? null : (double)BranchesHit / BranchesTotal;

    /// <summary>
    /// True when the underlying report carries branch data for this file. Lets formatters
    /// distinguish "100% branch coverage" from "no branch information was emitted" — relevant
    /// for MTP's default Cobertura emitter, which omits per-branch hits.
    /// </summary>
    public bool HasBranchData => BranchesTotal > 0;

    /// <summary>Line numbers with zero hits. Populated by parser — useful for AI test generation.</summary>
    public IReadOnlyList<int> UncoveredLines { get; init; } = [];

    /// <summary>Branch lines with partial coverage. Shows exactly which conditions need tests.</summary>
    public IReadOnlyList<BranchDetail> PartialBranches { get; init; } = [];

    /// <summary>
    /// Per-line hit counts (line number → hits). Cobertura emits the same line number under
    /// `&lt;methods&gt;&lt;method&gt;&lt;lines&gt;`, again under the class-level `&lt;lines&gt;`,
    /// and once more for every nested type or compiler-synthesized state-machine class sharing
    /// the filename. The parser merges those into a single set keyed by line number with the
    /// highest hit count seen, matching the per-line semantics every other Cobertura consumer
    /// (Codecov, ReportGenerator, Cobertura's own report.py) uses.
    /// </summary>
    public IReadOnlyDictionary<int, int> LineHits { get; init; } =
        new Dictionary<int, int>();

    /// <summary>
    /// Per-line branch hit/total counts. Needed for two things:
    ///   1. Cross-report merge: Codecov-style union-with-max on branches, instead of summing
    ///      across uploads (which double-counts when the same file is covered by both unit and
    ///      integration test runs).
    ///   2. Strict line classification: a line whose branches are not all exercised should be
    ///      reported as <see cref="LineStatus.Partial"/>, not <see cref="LineStatus.Hit"/>.
    /// Tuple semantics: (Covered, Total) per line. Lines with no branches are absent from the
    /// dictionary entirely (so a non-branched line is unambiguously not partial).
    /// <para>
    /// Merge semantics: <see cref="MergeWith"/> reconciles entries with <c>Math.Max</c> per
    /// component. A mismatched <c>Total</c> for the same line across reports usually means
    /// the reports came from different compile targets (e.g. one CI job built Release, another
    /// Debug) — the merge keeps the larger total and emits a
    /// <see cref="CoverageWarningKind.BranchTotalMismatch"/> warning so the divergence is
    /// observable rather than silent.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<int, (int Covered, int Total)> BranchesByLine { get; init; } =
        new Dictionary<int, (int Covered, int Total)>();

    /// <summary>
    /// Per-line, per-condition covered-outcome counts: line → (coverlet <c>condition number</c>
    /// → covered outcomes of that 2-way branch, 0–2). Populated only when the emitter ships
    /// <c>&lt;conditions&gt;&lt;condition number= coverage=/&gt;</c> children (coverlet does) AND they
    /// reconstruct the line aggregate as 2-outcome jumps. This is what makes cross-report branch
    /// <see cref="MergeWith"/> correct: two runs that each exercise a <em>different</em> condition of
    /// the same line union per <c>number</c> (Math.Max), instead of the line-level <c>Math.Max</c> on
    /// counts — which can't tell "both hit the same branch" from "each hit a different one" and
    /// under-reports the union (the classic false not-hit). Empty for emitters without per-condition
    /// detail or for non-2-way lines (switch jump tables); merge then falls back to the line aggregate.
    /// </summary>
    public IReadOnlyDictionary<int, IReadOnlyDictionary<int, int>> ConditionsByLine { get; init; } =
        new Dictionary<int, IReadOnlyDictionary<int, int>>();

    /// <summary>
    /// Codecov-style three-state classification for a single source line. Returns
    /// <see cref="LineStatus.Miss"/> for any line not in <see cref="LineHits"/> so callers
    /// can iterate over an arbitrary line range without first checking membership; use
    /// <see cref="TryGetLineStatus"/> when the tracked-vs-untracked distinction matters.
    /// </summary>
    public LineStatus GetLineStatus(int line)
    {
        if (!LineHits.TryGetValue(line, out var hits) || hits is 0)
            return LineStatus.Miss;
        if (BranchesByLine.TryGetValue(line, out var b) && b.Covered < b.Total)
            return LineStatus.Partial;
        return LineStatus.Hit;
    }

    /// <summary>
    /// Try-pattern variant of <see cref="GetLineStatus"/> that distinguishes "tracked but
    /// zero hits" from "not tracked at all". Returns <c>false</c> with <paramref name="status"/>
    /// set to <see cref="LineStatus.Miss"/> when the line is absent from <see cref="LineHits"/>;
    /// returns <c>true</c> with the actual status when the line is tracked.
    /// </summary>
    public bool TryGetLineStatus(int line, out LineStatus status)
    {
        if (!LineHits.ContainsKey(line))
        {
            status = LineStatus.Miss;
            return false;
        }
        status = GetLineStatus(line);
        return true;
    }

    /// <summary>
    /// Lines that were executed AND had all their branches exercised. Init-only; the parser's
    /// <c>Materialize</c> step and <see cref="MergeWith"/> compute it via the single-pass
    /// <see cref="ClassifyLines"/> helper. Hand-built records that supply
    /// <see cref="LineHits"/> + <see cref="BranchesByLine"/> must call <see cref="ClassifyLines"/>
    /// and set both this property and <see cref="PartiallyHitLines"/> at construction —
    /// a default of <c>0</c> will silently disagree with the keyspace of <see cref="LineHits"/>.
    /// </summary>
    public int StrictlyHitLines { get; init; }

    /// <summary>
    /// Lines that were executed but had at least one unexercised branch. Init-only; same
    /// construction contract as <see cref="StrictlyHitLines"/> — produced together by
    /// <see cref="ClassifyLines"/>.
    /// </summary>
    public int PartiallyHitLines { get; init; }

    /// <summary>
    /// Single pass over <paramref name="lineHits"/> that bins each executed line into the
    /// strictly-hit or partially-hit bucket. Replaces three independent walks of the same
    /// dict (one per call to <c>StrictlyHitLines</c>, <c>PartiallyHitLines</c>, and
    /// <c>CoverageReport.StrictLineRate</c>) with one — the difference is observable on
    /// 50 MB+ reports where every walk costs millions of <see cref="LineStatus"/> lookups.
    /// <para>
    /// Public so external callers hand-building a <see cref="FileCoverage"/> from raw
    /// dicts can fill <see cref="StrictlyHitLines"/> + <see cref="PartiallyHitLines"/>
    /// consistently in one call:
    /// <code>
    /// var (strict, partial) = FileCoverage.ClassifyLines(myHits, myBranches);
    /// var f = new FileCoverage("a.cs", linesHit, linesTotal, branchesHit, branchesTotal)
    /// {
    ///     LineHits = myHits, BranchesByLine = myBranches,
    ///     StrictlyHitLines = strict, PartiallyHitLines = partial
    /// };
    /// </code>
    /// </para>
    /// </summary>
    public static (int Strict, int Partial) ClassifyLines(
        IReadOnlyDictionary<int, int> lineHits,
        IReadOnlyDictionary<int, (int Covered, int Total)> branchesByLine)
    {
        var strict = 0;
        var partial = 0;
        foreach (var (line, hits) in lineHits)
        {
            if (hits is 0) continue;
            if (branchesByLine.TryGetValue(line, out var b) && b.Covered < b.Total)
                partial++;
            else
                strict++;
        }
        return (strict, partial);
    }

    /// <summary>
    /// Codecov-style strict line rate: <see cref="StrictlyHitLines"/> divided by
    /// <see cref="LinesTotal"/>. Stricter than <see cref="LineRate"/> — a line with
    /// <c>if (x &amp;&amp; y)</c> that only saw <c>x=true, y=true</c> is reported as
    /// not-fully-covered here, even though the line executed. Use this when you want the
    /// pessimistic-but-honest number; use <see cref="LineRate"/> when you need parity with
    /// Cobertura/Coverlet/ReportGenerator. Matches Codecov's <c>hits/(hits+partials+misses)</c>
    /// formula under the parser's invariant <c>LineHits.Count == LinesTotal</c>; hand-built
    /// <see cref="FileCoverage"/> records whose <see cref="LinesTotal"/> does not match the
    /// keyspace of <see cref="LineHits"/> diverge from the Codecov formula by exactly that
    /// gap.
    /// </summary>
    public double? StrictLineRate => LinesTotal is 0 ? null : (double)StrictlyHitLines / LinesTotal;

    /// <summary>
    /// Reconcile two reports of the same file into one. Returns the merged
    /// <see cref="FileCoverage"/> alongside a structured warnings list for anomalies the
    /// silent <c>Math.Max</c> reconciliation would otherwise hide — currently one
    /// <see cref="CoverageWarningKind.BranchTotalMismatch"/> per line whose <c>Total</c>
    /// disagrees between the two sides (usually a sign that the reports came from
    /// different compile targets, e.g. Release vs. Debug builds in different CI jobs).
    /// <para>
    /// Tuple return is the only signature: callers who don't care about warnings
    /// discard with <c>var (merged, _) = a.MergeWith(b)</c>. The convenience overload
    /// returning just <c>FileCoverage</c> was removed to keep the divergence-is-observable
    /// contract from being silently bypassed.
    /// </para>
    /// </summary>
    public (FileCoverage Merged, IReadOnlyList<CoverageWarning> Warnings) MergeWith(FileCoverage other)
    {
        var mergedHits = new Dictionary<int, int>(LineHits);
        foreach (var (line, hits) in other.LineHits)
            mergedHits[line] = mergedHits.TryGetValue(line, out var existing) ? Math.Max(existing, hits) : hits;

        // Branch dedup across reports: without per-branch location IDs (Cobertura's
        // `condition-coverage="n/m"` doesn't expose them), the safest defensible union is
        // Math.Max per line — at least N branches were exercised by some run, and Total is
        // the upper bound. Summing instead would double-count when the same file shows up in
        // multiple uploads (unit + integration test runs from the same CI workflow).
        var mergedBranches = new Dictionary<int, (int Covered, int Total)>(BranchesByLine);
        var warnings = new List<CoverageWarning>();

        // Correct branch union via per-condition identity: where BOTH reports carry condition
        // detail for a line, union the covered outcomes per coverlet `number` (Math.Max), then
        // recompute the line aggregate from the union (2-outcome jumps). This reconstructs the
        // true union when two runs exercise different conditions of the same line — the case
        // line-level Math.Max on counts gets wrong (the false not-hit).
        var mergedConditions = new Dictionary<int, IReadOnlyDictionary<int, int>>();
        foreach (var (line, mine) in ConditionsByLine)
        {
            if (!other.ConditionsByLine.TryGetValue(line, out var theirs)) continue;
            var union = new Dictionary<int, int>(mine);
            foreach (var (number, covered) in theirs)
                union[number] = union.TryGetValue(number, out var e) ? Math.Max(e, covered) : covered;
            mergedConditions[line] = union;
            mergedBranches[line] = (union.Values.Sum(), union.Count * 2);
        }

        foreach (var (line, b) in other.BranchesByLine)
        {
            if (mergedConditions.ContainsKey(line)) continue;   // already unioned per-condition above
            if (mergedBranches.TryGetValue(line, out var existing))
            {
                if (existing.Total != b.Total)
                {
                    var keep = Math.Max(existing.Total, b.Total);
                    warnings.Add(new CoverageWarning(
                        CoverageWarningKind.BranchTotalMismatch,
                        Path,
                        line,
                        $"Total {existing.Total} vs {b.Total} — keeping {keep}"));
                }
                mergedBranches[line] = (Math.Max(existing.Covered, b.Covered), Math.Max(existing.Total, b.Total));
            }
            else
            {
                mergedBranches[line] = b;
            }
        }

        var linesHit = 0;
        var uncovered = new List<int>();
        foreach (var (line, hits) in mergedHits)
        {
            if (hits > 0) linesHit++;
            else uncovered.Add(line);
        }
        uncovered.Sort();

        var branchesHit = 0;
        var branchesTotal = 0;
        var partialBranches = new List<BranchDetail>();
        foreach (var (line, b) in mergedBranches.OrderBy(static kv => kv.Key))
        {
            branchesHit += b.Covered;
            branchesTotal += b.Total;
            if (b.Covered < b.Total)
                partialBranches.Add(new BranchDetail(line, b.Covered, b.Total));
        }

        var (strict, partial) = ClassifyLines(mergedHits, mergedBranches);
        var merged = new FileCoverage(Path, linesHit, mergedHits.Count, branchesHit, branchesTotal)
        {
            LineHits = mergedHits,
            BranchesByLine = mergedBranches,
            ConditionsByLine = mergedConditions,
            UncoveredLines = uncovered,
            PartialBranches = partialBranches,
            StrictlyHitLines = strict,
            PartiallyHitLines = partial
        };
        return (merged, warnings);
    }
}

public sealed class CoverageReport
{
    public static readonly CoverageReport Empty = new([]);

    public CoverageReport(IReadOnlyList<FileCoverage> files) => Files = files;

    public IReadOnlyList<FileCoverage> Files { get; }

    /// <summary>
    /// Structured anomalies the parser/merger observed while producing this report.
    /// Empty when the source XML was well-formed and there were no cross-report
    /// divergences during merging. Init-only so consumers can rely on the value being
    /// stable for the report's lifetime. See <see cref="CoverageWarningKind"/> for the
    /// taxonomy and <see cref="CoverageWarning"/> for the payload shape.
    /// </summary>
    public IReadOnlyList<CoverageWarning> Warnings { get; init; } = [];

    public int TotalLines => Files.Sum(static f => f.LinesTotal);
    public int TotalLinesHit => Files.Sum(static f => f.LinesHit);
    public int TotalBranches => Files.Sum(static f => f.BranchesTotal);
    public int TotalBranchesHit => Files.Sum(static f => f.BranchesHit);

    /// <summary>
    /// Ratio of hit lines across the report, or <c>null</c> when it carries no line data at all —
    /// an empty report, a glob that matched nothing, a test step that produced no Cobertura.
    /// Returning 1.0 there (as this once did) renders "we measured nothing" as "we measured
    /// everything", which passes any gate. Null forces the caller to distinguish the two.
    /// </summary>
    public double? LineRate => TotalLines is 0 ? null : (double)TotalLinesHit / TotalLines;

    /// <summary>Ratio of hit branches, or <c>null</c> when the report carries no branch data.</summary>
    public double? BranchRate => TotalBranches is 0 ? null : (double)TotalBranchesHit / TotalBranches;

    /// <summary>True when the report carries any line data at all.</summary>
    public bool HasLineData => TotalLines > 0;

    /// <summary>True when any file in the report carries branch data.</summary>
    public bool HasBranchData => TotalBranches > 0;

    /// <summary>
    /// Codecov-style strict ratio across the whole report. A line counts only when its
    /// branches are all exercised — partials and misses both depress the rate. Sums the
    /// precomputed per-file <see cref="FileCoverage.StrictlyHitLines"/> rather than re-walking
    /// every <see cref="FileCoverage.LineHits"/> dict.
    /// </summary>
    public double? StrictLineRate => TotalLines is 0 ? null : (double)Files.Sum(static f => f.StrictlyHitLines) / TotalLines;

    /// <summary>
    /// Files measured below <paramref name="linePercent"/>. Files with no line data are not
    /// "below" anything and are omitted — they are unmeasured, not failing.
    /// </summary>
    public IEnumerable<FileCoverage> BelowPercent(double linePercent) =>
        Files.Where(f => f.LineRate is { } r && r * 100 < linePercent);

    /// <summary>
    /// Decide whether this report clears the given thresholds. Returns the full verdict rather
    /// than a bool so callers can tell a real failure from an unanswerable question from a gate
    /// that was never armed — three outcomes a bool collapses into "false".
    /// </summary>
    public GateResult Evaluate(double minLinePercent, double minBranchPercent = 0)
    {
        if (minLinePercent <= 0 && minBranchPercent <= 0)
            return new GateResult(GateOutcome.Disabled, LineRate, BranchRate, minLinePercent, minBranchPercent,
                "both thresholds are 0 - this gate cannot fail");

        if (!HasLineData)
            return new GateResult(GateOutcome.NoData, null, null, minLinePercent, minBranchPercent,
                "report carries no line data - nothing was measured");

        if (minBranchPercent > 0 && !HasBranchData)
            return new GateResult(GateOutcome.NoData, LineRate, null, minLinePercent, minBranchPercent,
                $"branch threshold of {minBranchPercent}% requested but the report carries no branch data");

        var lineOk = LineRate * 100 >= minLinePercent;
        var branchOk = minBranchPercent <= 0 || BranchRate * 100 >= minBranchPercent;

        return lineOk && branchOk
            ? new GateResult(GateOutcome.Pass, LineRate, BranchRate, minLinePercent, minBranchPercent, "thresholds met")
            : new GateResult(GateOutcome.Fail, LineRate, BranchRate, minLinePercent, minBranchPercent,
                !lineOk && !branchOk ? "line and branch coverage below threshold"
                : !lineOk ? "line coverage below threshold"
                : "branch coverage below threshold");
    }

    /// <summary>Remove files matching exclusion patterns. Returns a new report.</summary>
    public CoverageReport Exclude(IEnumerable<string> patterns) =>
        Exclude(patterns, keep: []);

    /// <summary>
    /// Remove files matching <paramref name="patterns"/>, then exempt any file whose path
    /// matches one of <paramref name="keep"/>. Useful when a default ruleset is too aggressive
    /// for a particular project — e.g. <c>--exclude-generated</c> drops <c>Program.cs</c> by
    /// default, but a top-level CLI tool's entire surface lives in <c>Program.cs</c> and the
    /// user wants to measure it.
    /// </summary>
    public CoverageReport Exclude(IEnumerable<string> patterns, IEnumerable<string> keep)
    {
        var rules = patterns.ToList();
        if (rules.Count is 0) return this;

        var keepRules = keep.ToList();

        var filtered = Files
            .Where(f =>
                keepRules.Any(k => f.Path.Contains(k, StringComparison.OrdinalIgnoreCase)) ||
                !rules.Any(p => f.Path.Contains(p, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return new CoverageReport(filtered) { Warnings = Warnings };
    }

    public static CoverageReport Merge(CoverageReport a, CoverageReport b)
    {
        var merged = new Dictionary<string, FileCoverage>(StringComparer.OrdinalIgnoreCase);
        // Carry forward whatever the two sides already observed; new divergences from
        // the per-file MergeWithWarnings calls below are appended in order.
        var warnings = new List<CoverageWarning>(a.Warnings.Count + b.Warnings.Count);
        warnings.AddRange(a.Warnings);
        warnings.AddRange(b.Warnings);

        foreach (var file in a.Files.Concat(b.Files))
        {
            if (merged.TryGetValue(file.Path, out var existing))
            {
                var (mergedFile, mergeWarnings) = existing.MergeWith(file);
                merged[file.Path] = mergedFile;
                warnings.AddRange(mergeWarnings);
            }
            else
            {
                merged[file.Path] = file;
            }
        }

        return new CoverageReport(merged.Values.ToList()) { Warnings = warnings };
    }
}

/// <summary>
/// Well-known exclusion patterns from BuildCoverage.cs — battle-tested.
/// Generated code, build output, migrations, and async state machines inflate coverage numbers.
/// </summary>
public static class ExclusionRules
{
    public static readonly IReadOnlyList<string> WellKnown =
    [
        ".g.cs",          // source generators
        ".designer.cs",   // WinForms/WPF designers
        "/obj/",          // build intermediates
        "/bin/",          // build output
        "/Migrations/",   // EF Core migrations
        // No "d__" rule: coverlet emits async state-machine *class names* (<Method>d__0) but the
        // <class filename=> is the plain source file, and Exclude matches on Path (the filename) —
        // so a "d__" path rule is structurally dead (matches nothing). The state machine's branches
        // fuse into the real file; suppress them via coverlet config ([ExcludeFromCodeCoverage] /
        // runsettings), which removes them at the source, not via a path filter that can't fire.
        "GlobalUsings",   // auto-generated global usings
        "/Program.cs",    // ASP.NET Core / generic host bootstrap — opt back in with --keep Program.cs
    ];

    /// <summary>
    /// Test code and the fixtures it is built from. Kept separate from <see cref="WellKnown"/>
    /// because excluding it changes the number rather than correcting it, and that is a policy
    /// call: a project may legitimately want its test helpers held to a standard.
    /// </summary>
    /// <remarks>
    /// Coverage asks "did the tests exercise this line". Asked of the fixtures the tests are built
    /// from, it answers itself — every helper a suite touches is covered by construction — so the
    /// number carries no information about product risk while still moving the aggregate a gate
    /// decides on. Note that a <c>.Tests</c> suffix rule does not match a <c>TestSupport</c>
    /// assembly; both spellings are listed here because that gap is easy to miss and silent.
    /// </remarks>
    public static readonly IReadOnlyList<string> TestCode =
    [
        ".Tests/",        // xUnit/NUnit/MSTest projects, forward-slash paths
        ".Tests\\",       // ...and Windows-emitted paths
        "TestSupport/",   // shared fixtures — NOT matched by a ".Tests" rule
        "TestSupport\\",
        "/TestFixtures/",
        "\\TestFixtures\\",
    ];
}
