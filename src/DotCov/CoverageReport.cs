using System.Collections.ObjectModel;

namespace DotCov;

public readonly record struct BranchDetail(int Line, int Covered, int Total);

/// <summary>
/// Codecov-style three-state line classification. A line that was executed but whose
/// conditional branches were not all exercised is <see cref="Partial"/> — distinct from
/// <see cref="Hit"/> (fully exercised) and <see cref="Miss"/> (not executed at all).
/// Used for the strict coverage view; the canonical <see cref="FileCoverage.LineRate"/>
/// treats partials as hit to stay aligned with the Cobertura source-of-truth.
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
    /// A Cobertura <c>&lt;line&gt;</c> carried invalid branch data. Either its branch
    /// aggregate is unusable — <c>branch="true"</c> without a <c>condition-coverage</c>
    /// attribute, a value that doesn't match the <c>(covered/total)</c> shape, numbers that
    /// overflow <see cref="int"/>, or more covered outcomes than its total (e.g.
    /// <c>150% (3/2)</c>) — and the parser counts no branch for the line; or its
    /// <c>&lt;condition&gt;</c> detail is unparseable or disagrees with that line's own
    /// aggregate, and the parser drops the detail and keeps the aggregate. Either way this
    /// warning makes the omission observable, so emitter regressions can neither silently zero
    /// out branch coverage nor inflate it.
    /// </summary>
    MalformedConditionCoverage,

    /// <summary>
    /// Per-condition detail for the same source line arrived with different condition
    /// <c>number</c> sets — from two reports, or from two <c>&lt;class&gt;</c> blocks of one
    /// document. Coverlet's condition numbers are IL branch offsets, stable only within one
    /// IL method of one build, so unioning mismatched sets would invent branch totals no
    /// emitter reported. The parser and <see cref="FileCoverage.MergeWith"/> apply the same
    /// rule: the line falls back to its raw line-level aggregate, and the downgrade is
    /// recorded here once per line and document or merge — so the same measurements give the
    /// same numbers however the XML packages them.
    /// </summary>
    ConditionIdentityMismatch,

    /// <summary>
    /// A Cobertura <c>&lt;line&gt;</c> carried a <c>hits</c> attribute that could not be
    /// parsed as an integer. The parser records the line with zero hits — the conservative
    /// reading — and surfaces the degradation here instead of silently flipping a possibly
    /// covered line to a miss. An <em>absent</em> <c>hits</c> attribute is not malformed
    /// (some emitters omit it on summary lines) and stays a warning-free zero.
    /// </summary>
    MalformedHits,

    /// <summary>
    /// File identity could not be established unambiguously. Emitted in three situations:
    /// (1) under <see cref="FileIdentityMode.Lexical"/>, a relative filename in a report that
    /// declares several <c>&lt;source&gt;</c> roots — one warning per file, naming the
    /// candidate roots; the file is keyed under the first and listed in
    /// <see cref="CoverageReport.AmbiguousFiles"/>, which turns every gate into NoData;
    /// (2) under <see cref="FileIdentityMode.ProbeSources"/>, a relative filename that exists
    /// under no declared root or under several on the reading machine — one warning per
    /// document, and the first root applies; (3) <see cref="CoverageReport.Merge"/> combined
    /// reports whose source roots differ and found files unique to each side that share the
    /// same file name — the same source file uploaded under two path conventions (e.g.
    /// Coverlet default vs <c>DeterministicSourcePaths</c>) looks exactly like this, and if
    /// that is what happened its coverage is double-counted in the merged totals. The merge
    /// keeps the entries separate — fusing them would silently erase a genuinely distinct
    /// file's misses — and surfaces the ambiguity here instead.
    /// </summary>
    FileIdentityAmbiguous
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

    // A `default(FileCoverage)` — array growth, a Dictionary.TryGetValue miss, `default` — never
    // runs the property initializers below, so every collection accessor null-coalesces to a
    // shared empty singleton: a default instance behaves as an empty file instead of violating
    // its own non-nullable annotations and NRE-ing in GetLineStatus/MergeWith.
    private static readonly IReadOnlyDictionary<int, int> NoHits =
        ReadOnlyDictionary<int, int>.Empty;

    private static readonly IReadOnlyDictionary<int, (int Covered, int Total)> NoBranches =
        ReadOnlyDictionary<int, (int Covered, int Total)>.Empty;

    private static readonly IReadOnlyDictionary<int, IReadOnlyDictionary<int, int>> NoConditions =
        ReadOnlyDictionary<int, IReadOnlyDictionary<int, int>>.Empty;

    /// <summary>Line numbers with zero hits. Populated by parser — useful for AI test generation.</summary>
    public IReadOnlyList<int> UncoveredLines { get => field ?? []; init; } = [];

    /// <summary>Branch lines with partial coverage. Shows exactly which conditions need tests.</summary>
    public IReadOnlyList<BranchDetail> PartialBranches { get => field ?? []; init; } = [];

    /// <summary>
    /// Per-line hit counts (line number → hits). Cobertura emits the same line number under
    /// `&lt;methods&gt;&lt;method&gt;&lt;lines&gt;`, again under the class-level `&lt;lines&gt;`,
    /// and once more for every nested type or compiler-synthesized state-machine class sharing
    /// the filename. The parser merges those into a single set keyed by line number with the
    /// highest hit count seen, matching the per-line semantics every other Cobertura consumer
    /// (Codecov, ReportGenerator, Cobertura's own report.py) uses.
    /// </summary>
    public IReadOnlyDictionary<int, int> LineHits { get => field ?? NoHits; init; } = NoHits;

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
    public IReadOnlyDictionary<int, (int Covered, int Total)> BranchesByLine
    {
        get => field ?? NoBranches;
        init;
    } = NoBranches;

    /// <summary>
    /// Merge-state twin of <see cref="BranchesByLine"/>: the raw line-level union with NO
    /// condition-derived overlay applied. <see cref="MergeWith"/> folds these — never the
    /// presented values — because an overlaid aggregate baked into merge state cannot be
    /// un-maxed when a later report poisons the line's condition identity, which would make the
    /// merged result depend on fold order. Unset (null field) for hand-built records, where
    /// the presented map IS the raw map; <see cref="FromLineData"/> sets it whenever an
    /// overlay changed the presentation.
    /// </summary>
    internal IReadOnlyDictionary<int, (int Covered, int Total)> RawBranchesByLine
    {
        get => field ?? BranchesByLine;
        init;
    }

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
    /// <para>
    /// Merge semantics: <see cref="MergeWith"/> unions per condition number only when both sides
    /// carry the <em>same</em> number set for the line — the only case where coverlet's IL-offset
    /// numbers provably identify the same branch. Mismatched sets emit a
    /// <see cref="CoverageWarningKind.ConditionIdentityMismatch"/> warning and permanently
    /// poison the line's condition identity: the merged entry becomes an <em>empty</em>
    /// dictionary (real entries are never empty), an absorbing sentinel no later merge may
    /// union or resurrect detail through — otherwise the merged aggregate would depend on the
    /// order reports are folded in. A poisoned line contributes only its raw line-level
    /// aggregate. Detail present on only one side is carried forward unchanged so a later
    /// merge can still union it; the line aggregate never regresses below what the
    /// line-level union observed. The parser applies the same rule to the
    /// <c>&lt;class&gt;</c> blocks of one document, so one document with two blocks and two
    /// merged documents give the same numbers and the same warnings.
    /// </para>
    /// <para>
    /// Two different checks guard the derived aggregate. Within one document, a
    /// <c>&lt;line&gt;</c>'s 2-way detail must reproduce that line's own
    /// <c>condition-coverage</c> exactly: its covered outcomes must sum to the line's covered
    /// count. Detail that disagrees is dropped with a
    /// <see cref="CoverageWarningKind.MalformedConditionCoverage"/> warning, because one run
    /// cannot have covered more than it reports. Across reports the derived aggregate MAY
    /// exceed either side's: two runs that exercised different conditions of the same line
    /// legitimately raise each other, which is the point of the per-condition union.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<int, IReadOnlyDictionary<int, int>> ConditionsByLine
    {
        get => field ?? NoConditions;
        init;
    } = NoConditions;

    /// <summary>
    /// Codecov-style three-state classification for a single source line. Returns
    /// <see cref="LineStatus.Miss"/> for any line not in <see cref="LineHits"/> so callers
    /// can iterate over an arbitrary line range without first checking membership; use
    /// <see cref="TryGetLineStatus"/> when the tracked-vs-untracked distinction matters.
    /// </summary>
    public LineStatus GetLineStatus(int line)
    {
        if (!LineHits.TryGetValue(line, out var hits) || hits <= 0)
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
    /// strictly-hit or partially-hit bucket, so <c>StrictlyHitLines</c>, <c>PartiallyHitLines</c>,
    /// and <c>CoverageReport.StrictLineRate</c> share one walk of the dict instead of three — the
    /// difference is observable on 50 MB+ reports where every walk costs millions of
    /// <see cref="LineStatus"/> lookups.
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
            if (hits <= 0) continue;
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
    /// silent <c>Math.Max</c> reconciliation would otherwise hide — one
    /// <see cref="CoverageWarningKind.BranchTotalMismatch"/> per line whose <c>Total</c>
    /// disagrees between the two sides (usually a sign that the reports came from
    /// different compile targets, e.g. Release vs. Debug builds in different CI jobs), and
    /// one <see cref="CoverageWarningKind.ConditionIdentityMismatch"/> per line whose
    /// per-condition number sets disagree (same cause; the per-condition union is skipped
    /// for that line because the numbers do not identify the same branches).
    /// <para>
    /// Tuple return is the only signature, so the divergence-is-observable contract cannot be
    /// silently bypassed: callers who don't care about warnings discard with
    /// <c>var (merged, _) = a.MergeWith(b)</c>.
    /// </para>
    /// <para>
    /// Counts not backed by per-line data: a record built from counts alone (the positional
    /// constructor) has no lines to union. For each dimension — lines, and branches — whose
    /// counts a side's per-line data does not reproduce (<see cref="LineHits"/> for
    /// <see cref="LinesHit"/>/<see cref="LinesTotal"/>, <see cref="BranchesByLine"/> for
    /// <see cref="BranchesHit"/>/<see cref="BranchesTotal"/>), the merged hit and total are
    /// each the <c>Math.Max</c> of those unbacked counts and what the per-line union yields,
    /// so a merge never reports less than either side vouched for, and reconciling a
    /// count-only record with itself keeps it. The unbacked counts are carried as merge
    /// state rather than re-read from the merged counts, so the result does not depend on
    /// the order reports are folded in. Records whose per-line data backs their counts on
    /// both sides — everything the parser builds — merge exactly by line as described above.
    /// </para>
    /// </summary>
    public (FileCoverage Merged, IReadOnlyList<CoverageWarning> Warnings) MergeWith(FileCoverage other)
    {
        var mergedHits = new Dictionary<int, int>(LineHits);
        foreach (var (line, hits) in other.LineHits)
            mergedHits[line] = mergedHits.TryGetValue(line, out var existing) ? Math.Max(existing, hits) : hits;

        var warnings = new List<CoverageWarning>();
        var mergedBranches = MergeBranches(other, warnings);
        var mergedConditions = MergeConditions(other, warnings);

        return (FromLineData(Path, mergedHits, mergedBranches, mergedConditions,
            MaxCounts(LineCountFloor, other.LineCountFloor),
            MaxCounts(BranchCountFloor, other.BranchCountFloor)), warnings);
    }

    /// <summary>
    /// Merge-state floor for the line counts: the largest (hit, total) carried by any merged
    /// record whose per-line data did not back them. Stored by <see cref="FromLineData"/>
    /// (zero for parser output); for a hand-built record it is its own counts when
    /// <see cref="LineHits"/> does not reproduce them, and zero when it does.
    /// </summary>
    internal (int Hit, int Total) LineCountFloor
    {
        get => _lineCountFloor ?? (LinesBackedByLineData ? default : (LinesHit, LinesTotal));
        init => _lineCountFloor = value;
    }

    /// <summary>Branch twin of <see cref="LineCountFloor"/>, judged against <see cref="BranchesByLine"/>.</summary>
    internal (int Hit, int Total) BranchCountFloor
    {
        get => _branchCountFloor ?? (BranchesBackedByLineData ? default : (BranchesHit, BranchesTotal));
        init => _branchCountFloor = value;
    }

    private readonly (int Hit, int Total)? _lineCountFloor;
    private readonly (int Hit, int Total)? _branchCountFloor;

    private bool LinesBackedByLineData =>
        LineHits.Count == LinesTotal && LineHits.Values.Count(static hits => hits > 0) == LinesHit;

    private bool BranchesBackedByLineData =>
        BranchesByLine.Values.Sum(static b => b.Covered) == BranchesHit &&
        BranchesByLine.Values.Sum(static b => b.Total) == BranchesTotal;

    private static (int Hit, int Total) MaxCounts((int Hit, int Total) a, (int Hit, int Total) b) =>
        (Math.Max(a.Hit, b.Hit), Math.Max(a.Total, b.Total));

    /// <summary>Line-level branch union of two reports; a line whose <c>Total</c> differs adds a warning.</summary>
    private Dictionary<int, (int Covered, int Total)> MergeBranches(FileCoverage other, List<CoverageWarning> warnings)
    {
        // Branch dedup across reports: without per-branch location IDs (Cobertura's
        // `condition-coverage="n/m"` doesn't expose them), the safest defensible union is
        // Math.Max per line — at least N branches were exercised by some run, and Total is
        // the upper bound. Summing instead would double-count when the same file shows up in
        // multiple uploads (unit + integration test runs from the same CI workflow).
        //
        // Folded over the RAW line-level maps, not the presented ones: keywise Math.Max over
        // raw values is associative, and the condition-derived overlay is applied only at
        // presentation time (FromLineData). Persisting the overlay into merge state would make
        // the outcome fold-order-dependent — an overlaid Math.Max survives a later condition-
        // identity poisoning and cannot be un-maxed.
        var mergedBranches = new Dictionary<int, (int Covered, int Total)>(RawBranchesByLine);

        foreach (var (line, b) in other.RawBranchesByLine)
        {
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

        return mergedBranches;
    }

    /// <summary>Per-condition union of two reports; a line whose condition numbers disagree warns and is poisoned.</summary>
    private Dictionary<int, IReadOnlyDictionary<int, int>> MergeConditions(FileCoverage other, List<CoverageWarning> warnings)
    {
        // Correct branch union via per-condition identity: where both reports carry condition
        // detail for a line WITH the same condition-number set, union the covered outcomes per
        // coverlet `number` (Math.Max). FromLineData later overlays the derived aggregate onto
        // the presented line aggregate — reconstructing the true union when two runs exercise
        // different conditions of the same line, the case line-level Math.Max on counts gets
        // wrong (the false not-hit). Condition numbers are IL branch offsets, stable only
        // within one build, so mismatched sets must NOT union: that would invent a total
        // neither emitter reported (the same invariant Materialize enforces within a single
        // report). A mismatch warns and POISONS the line with the empty-dict sentinel: an
        // absorbing marker no later merge may union or resurrect detail through. Without it,
        // a mismatched middle report would be forgotten while a later one-sided carry
        // resurrected detail, making the fold order decide the merged aggregate.
        var mergedConditions = new Dictionary<int, IReadOnlyDictionary<int, int>>();
        foreach (var (line, mine) in ConditionsByLine)
        {
            if (other.ConditionsByLine.TryGetValue(line, out var theirs))
            {
                if (mine.Count is 0 || theirs.Count is 0)
                {
                    // Already poisoned on a side by an earlier merge (which warned then):
                    // absorbing, and no redundant per-merge warning.
                    mergedConditions[line] = PoisonedConditions;
                }
                else if (mine.Count == theirs.Count && mine.Keys.All(theirs.ContainsKey))
                {
                    var union = new Dictionary<int, int>(mine.Count);
                    foreach (var (number, covered) in mine)
                        union[number] = Math.Max(covered, theirs[number]);
                    mergedConditions[line] = union;
                }
                else
                {
                    warnings.Add(new CoverageWarning(
                        CoverageWarningKind.ConditionIdentityMismatch,
                        Path,
                        line,
                        $"condition numbers [{string.Join(",", mine.Keys.Order())}] vs " +
                        $"[{string.Join(",", theirs.Keys.Order())}] - using the line aggregate"));
                    mergedConditions[line] = PoisonedConditions;
                }
            }
            else
            {
                // One-sided detail (the other emitter shipped no <conditions> for this line):
                // carry it forward so a later merge can still union per condition instead of
                // degrading to line-level Math.Max forever — and so merge order cannot change
                // the outcome. A poisoned entry stays poisoned.
                mergedConditions[line] = mine.Count is 0 ? PoisonedConditions : new Dictionary<int, int>(mine);
            }
        }

        foreach (var (line, theirs) in other.ConditionsByLine)
        {
            if (ConditionsByLine.ContainsKey(line)) continue;   // handled above
            mergedConditions[line] = theirs.Count is 0 ? PoisonedConditions : new Dictionary<int, int>(theirs);
        }

        return mergedConditions;
    }

    /// <summary>
    /// The condition-identity poison sentinel: an empty condition set. Safe to overload as a
    /// sentinel because real detail always holds at least one condition — only a mismatch
    /// downgrade, here or in the parser's per-document union, produces an empty entry.
    /// </summary>
    private static readonly IReadOnlyDictionary<int, int> PoisonedConditions =
        ReadOnlyDictionary<int, int>.Empty;

    /// <summary>
    /// Shared construction tail for the parser's <c>Materialize</c> and <see cref="MergeWith"/>:
    /// derives the uncovered list, branch aggregates, partial branches, and strict classification
    /// from the raw dicts, so parse and merge can never derive them differently.
    /// Wraps the dicts read-only so internal accumulator state never escapes mutable behind the
    /// <c>IReadOnly*</c> surface (a downcast mutation would silently desynchronize
    /// <see cref="LineHits"/> from the precomputed aggregates). The count floors (zero for the
    /// parser) raise each file-level count to at least the unbacked counts a merge carried.
    /// </summary>
    internal static FileCoverage FromLineData(
        string path,
        Dictionary<int, int> lineHits,
        Dictionary<int, (int Covered, int Total)> branchesByLine,
        Dictionary<int, IReadOnlyDictionary<int, int>> conditionsByLine,
        (int Hit, int Total) lineCountFloor = default,
        (int Hit, int Total) branchCountFloor = default)
    {
        var linesHit = 0;
        var uncovered = new List<int>();
        foreach (var (line, hits) in lineHits)
        {
            if (hits > 0) linesHit++;
            else uncovered.Add(line);
        }

        uncovered.Sort();

        // Presented branch view: overlay the condition-derived aggregate onto the raw
        // line-level value, component-wise Math.Max. Condition detail can only raise the
        // covered count (different conditions exercised by different runs or class blocks),
        // never regress a count the line-level union already observed from a condition-less
        // side. Poisoned lines (empty sentinel — see MergeWith) keep the raw aggregate:
        // their per-condition identity is untrustworthy, so nothing may be derived from it.
        // The overlay lives HERE, not in merge state, so MergeWith stays associative.
        var presented = branchesByLine;
        if (conditionsByLine.Count > 0)
        {
            presented = new Dictionary<int, (int Covered, int Total)>(branchesByLine);
            foreach (var (line, conds) in conditionsByLine)
            {
                if (conds.Count is 0) continue;   // poisoned: raw line-level aggregate only
                var derived = (Covered: conds.Values.Sum(), Total: conds.Count * 2);
                presented[line] = presented.TryGetValue(line, out var b)
                    ? (Math.Max(derived.Covered, b.Covered), Math.Max(derived.Total, b.Total))
                    : derived;
            }
        }

        var branchesHit = 0;
        var branchesTotal = 0;
        var partialBranches = new List<BranchDetail>();
        foreach (var (line, b) in presented.OrderBy(static kv => kv.Key))
        {
            branchesHit += b.Covered;
            branchesTotal += b.Total;
            if (b.Covered < b.Total)
                partialBranches.Add(new BranchDetail(line, b.Covered, b.Total));
        }

        var (strict, partial) = ClassifyLines(lineHits, presented);
        // Counts never fall below what an unbacked count-only side vouched for (see MergeWith).
        return new FileCoverage(path,
            Math.Max(linesHit, lineCountFloor.Hit), Math.Max(lineHits.Count, lineCountFloor.Total),
            Math.Max(branchesHit, branchCountFloor.Hit), Math.Max(branchesTotal, branchCountFloor.Total))
        {
            LineCountFloor = lineCountFloor,
            BranchCountFloor = branchCountFloor,
            LineHits = new ReadOnlyDictionary<int, int>(lineHits),
            BranchesByLine = new ReadOnlyDictionary<int, (int Covered, int Total)>(presented),
            RawBranchesByLine = new ReadOnlyDictionary<int, (int Covered, int Total)>(branchesByLine),
            ConditionsByLine = new ReadOnlyDictionary<int, IReadOnlyDictionary<int, int>>(conditionsByLine),
            UncoveredLines = uncovered,
            PartialBranches = partialBranches,
            StrictlyHitLines = strict,
            PartiallyHitLines = partial
        };
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

    /// <summary>
    /// The <see cref="ReportInput.SourceName"/> of every document this report was parsed and
    /// merged from, in merge order; empty for a report read from a bare stream or built by hand.
    /// Every JSON document lists them as <c>inputs</c>, so a consumer can see which reports a
    /// number came from.
    /// </summary>
    public IReadOnlyList<string> Inputs { get; init; } = [];

    /// <summary>
    /// The <c>&lt;source&gt;</c> roots the report declared, normalized (separators flipped,
    /// drive letter uppercased, trailing <c>/</c> trimmed above one character) and
    /// deduplicated, in document order. Relative class filenames were resolved against the
    /// first of these to form <see cref="FileCoverage.Path"/>, so the same relative name
    /// under two different roots (monorepo services, per-job checkouts) stays two distinct
    /// files. A no-op root (<c>.</c>, <c>./</c>) declared alongside real roots appears as an
    /// empty-string entry — it holds the resolution slot (a no-op FIRST root leaves relative
    /// filenames unprefixed) and keeps the mixed declaration distinguishable at merge time;
    /// a report whose only root is a no-op exposes no roots at all. Empty for hand-built
    /// reports and documents without a <c>&lt;sources&gt;</c> element. <see cref="Merge"/>
    /// compares the two sides' roots to detect cross-convention identity ambiguity and unions
    /// them on the merged report.
    /// </summary>
    public IReadOnlyList<string> SourceRoots { get; init; } = [];

    /// <summary>
    /// Files whose identity the parse could not resolve (<see cref="FileIdentityMode.Lexical"/>
    /// with several declared roots): each is present in <see cref="Files"/> under its
    /// deterministic first-root key, but nothing proves that root is its own. While any remain,
    /// <see cref="Evaluate"/> answers <see cref="GateOutcome.NoData"/>. <see cref="Exclude(IEnumerable{string}, IEnumerable{string})"/>
    /// drops the excluded ones; <see cref="Merge"/> unions both sides.
    /// </summary>
    public IReadOnlyList<AmbiguousFile> AmbiguousFiles { get; init; } = [];

    /// <summary>
    /// The exclusions applied to this report and the files they removed;
    /// <see cref="FileExclusion.None"/> until <see cref="Exclude(IEnumerable{string}, IEnumerable{string})"/> runs.
    /// </summary>
    public FileExclusion Exclusion { get; init; } = FileExclusion.None;

    public int TotalLines => Files.Sum(static f => f.LinesTotal);
    public int TotalLinesHit => Files.Sum(static f => f.LinesHit);
    public int TotalBranches => Files.Sum(static f => f.BranchesTotal);
    public int TotalBranchesHit => Files.Sum(static f => f.BranchesHit);

    /// <summary>
    /// Ratio of hit lines across the report, or <c>null</c> when it carries no line data at all —
    /// an empty report, a glob that matched nothing, a test step that produced no Cobertura.
    /// Returning 1.0 there would render "we measured nothing" as "we measured everything",
    /// which passes any gate. Null forces the caller to distinguish the two.
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

    /// <summary>The number of offenders a gate lists unless told otherwise (<c>--top</c>).</summary>
    public const int DefaultOffenderLimit = 20;

    /// <summary>
    /// What to fix next after a <see cref="GateOutcome.Fail"/>: every file below the threshold
    /// of a dimension <paramref name="gate"/> failed, worst first — the lowest of its failing
    /// rates, ties by ordinal path. A file with no data in a dimension is not below it, and a
    /// passing, unmeasurable, or disabled gate blames no file at all. Uses the same
    /// epsilon-tolerant comparison as <see cref="Evaluate"/>, so a file exactly at a threshold
    /// is never listed.
    /// </summary>
    public IReadOnlyList<FileCoverage> Offenders(GateResult gate)
    {
        if (gate.Outcome is not GateOutcome.Fail) return [];

        return Files
            .Select(f => (File: f, Worst: Min(
                gate.LineBelowThreshold ? Below(f.LineRate, gate.MinLinePercent) : null,
                gate.BranchBelowThreshold ? Below(f.BranchRate, gate.MinBranchPercent) : null)))
            .Where(static o => o.Worst is not null)
            .OrderBy(static o => o.Worst)
            .ThenBy(static o => o.File.Path, StringComparer.Ordinal)
            .Select(static o => o.File)
            .ToList();

        static double? Below(double? rate, double minPercent) =>
            rate is { } r && !GateResult.MeetsThreshold(r, minPercent) ? r : null;

        static double? Min(double? line, double? branch) =>
            line is { } l && branch is { } b ? Math.Min(l, b) : line ?? branch;
    }

    /// <summary>
    /// Decide whether this report clears the given thresholds. Returns the full verdict rather
    /// than a bool so callers can tell a real failure from an unanswerable question from a gate
    /// that was never armed — three outcomes a bool collapses into "false". A report with
    /// <see cref="AmbiguousFiles"/> is <see cref="GateOutcome.NoData"/>: its numbers may count a
    /// file under a root that is not its own.
    /// </summary>
    public GateResult Evaluate(double minLinePercent, double minBranchPercent = 0)
    {
        if (minLinePercent <= 0 && minBranchPercent <= 0)
            return new GateResult(GateOutcome.Disabled, LineRate, BranchRate, minLinePercent, minBranchPercent,
                "no positive threshold - this gate cannot fail");

        if (!HasLineData)
            return new GateResult(GateOutcome.NoData, null, null, minLinePercent, minBranchPercent,
                "report carries no line data - nothing was measured");

        if (AmbiguousFiles.Count > 0)
            return new GateResult(GateOutcome.NoData, LineRate, BranchRate, minLinePercent, minBranchPercent,
                AmbiguousFile.GateReason(AmbiguousFiles));

        if (minBranchPercent > 0 && !HasBranchData)
            return new GateResult(GateOutcome.NoData, LineRate, null, minLinePercent, minBranchPercent,
                FormattableString.Invariant($"branch threshold of {minBranchPercent}% requested but the report carries no branch data"));

        // Epsilon-tolerant so an exactly-met threshold passes: 29/100 lines against
        // --min-line 29 is a pass, not the self-contradictory "FAIL: line 29.0% (min 29%)"
        // that a raw `rate * 100 >= min` produces when the division lands a ulp low.
        // LineRate exists here: HasLineData was checked above. BranchRate may not: a NaN branch
        // threshold is neither "> 0" (so no NoData above) nor "<= 0", and must fail, not throw.
        var lineOk = GateResult.MeetsThreshold(LineRate!.Value, minLinePercent);
        var branchOk = minBranchPercent <= 0 ||
                       (BranchRate is { } br && GateResult.MeetsThreshold(br, minBranchPercent));

        return lineOk && branchOk
            ? new GateResult(GateOutcome.Pass, LineRate, BranchRate, minLinePercent, minBranchPercent, "thresholds met")
            : new GateResult(GateOutcome.Fail, LineRate, BranchRate, minLinePercent, minBranchPercent,
                !lineOk && !branchOk ? "line and branch coverage below threshold"
                : !lineOk ? "line coverage below threshold"
                : "branch coverage below threshold");
    }

    /// <summary>
    /// Remove files matching exclusion patterns. Returns a new report. Patterns are
    /// case-insensitive <em>substring</em> matches against the file path, not globs — see the
    /// two-parameter overload for the exact matching rules.
    /// </summary>
    public CoverageReport Exclude(IEnumerable<string> patterns) =>
        Exclude(patterns, keep: []);

    /// <summary>
    /// Remove files matching <paramref name="patterns"/>, then exempt any file whose path
    /// matches one of <paramref name="keep"/>. Useful when a default ruleset is too aggressive
    /// for a particular project — e.g. <c>--exclude-generated</c> drops <c>Program.cs</c> by
    /// default, but a top-level CLI tool's entire surface lives in <c>Program.cs</c> and the
    /// user wants to measure it.
    /// </summary>
    /// <remarks>
    /// Patterns are case-insensitive <em>substring</em> matches, not globs: <c>Program.cs</c>
    /// also matches <c>MyProgram.cs</c>, and a glob like <c>src/**/*.g.cs</c> matches nothing.
    /// Matching runs against the path with a leading <c>/</c> prepended, so a rule anchored
    /// with a leading separator (<c>/Program.cs</c>, <c>/obj/</c>) also matches at the top
    /// level of a prefix-less path — emitters disagree on whether <c>filename</c> carries a
    /// directory prefix, and the same file must match either way.
    /// </remarks>
    public CoverageReport Exclude(IEnumerable<string> patterns, IEnumerable<string> keep)
    {
        var rules = patterns.ToList();
        if (rules.Count is 0) return this;

        var keepRules = keep.ToList();
        var (kept, excluded) = ExclusionRules.Partition(Files, static f => f.Path, rules, keepRules);

        return new CoverageReport(kept)
        {
            Warnings = Warnings,
            Inputs = Inputs,
            SourceRoots = SourceRoots,
            AmbiguousFiles = ExclusionRules.Remaining(AmbiguousFiles, excluded),
            Exclusion = Exclusion.Then(rules, keepRules, excluded)
        };
    }

    public static CoverageReport Merge(CoverageReport a, CoverageReport b)
    {
        // Ordinal, matching the parser's file keying: case-differing paths are distinct files
        // on the case-sensitive filesystems the format's native emitters run on, and fusing
        // them would silently erase the less-covered file's misses.
        var merged = new Dictionary<string, FileCoverage>(StringComparer.Ordinal);
        // Carry forward whatever the two sides already observed; new divergences from
        // the per-file MergeWith calls below are appended in order.
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

        WarnOnAmbiguousFileIdentity(a, b, warnings);

        var exclusion = a.Exclusion.Union(b.Exclusion);

        return new CoverageReport(merged.Values.ToList())
        {
            Warnings = warnings,
            Inputs = [.. a.Inputs, .. b.Inputs],
            // Dedup by normalized identity, not raw spelling: RootsDiffer compares normalized
            // sequences, so a raw union of c:\repo and C:/repo/ would keep two entries and
            // permanently re-flag every later merge as ambiguous.
            SourceRoots = a.SourceRoots.Concat(b.SourceRoots)
                .DistinctBy(PathIdentity.NormalizeRoot, StringComparer.Ordinal).ToList(),
            AmbiguousFiles = AmbiguousFile.Union(a.AmbiguousFiles.Concat(b.AmbiguousFiles)),
            // The other input may reintroduce a previously excluded file. Keep the applied
            // rules, but list only paths that remain absent, using the same identity as Files.
            Exclusion = exclusion with
            {
                ExcludedFiles = exclusion.ExcludedFiles.Where(p => !merged.ContainsKey(p)).ToArray()
            }
        };
    }

    /// <summary>
    /// The honest half of the cross-convention file-identity problem: the SAME source file
    /// uploaded under two path conventions (Coverlet default emits
    /// <c>&lt;source&gt;/&lt;/source&gt;</c> + machine-absolute filenames;
    /// DeterministicSourcePaths emits <c>&lt;source&gt;/_/&lt;/source&gt;</c> + repo-relative
    /// ones) resolves to two different keys no root arithmetic can unify, so the merge keeps
    /// two entries and the file double-counts. That cannot be fixed without probing a disk
    /// this machine may not have — but it CAN be surfaced: when the two sides' declared roots
    /// differ, any file unique to one side whose file name (whole final path segment, Ordinal)
    /// matches a file unique to the other side gets a
    /// <see cref="CoverageWarningKind.FileIdentityAmbiguous"/> warning naming both keys.
    /// Same-roots merges (the overwhelmingly common same-pipeline case) skip the scan, so
    /// partitioned test runs never see noise from genuinely distinct same-named files.
    /// "Same" is judged by <see cref="PathIdentity.RootsDiffer"/> — normalized sequence
    /// identity, not raw spelling — so drive-letter case, a trailing slash, or a
    /// hand-built report's unnormalized roots never fake a convention change; reordered
    /// roots still count as differing because relative filenames resolve against the first.
    /// </summary>
    private static void WarnOnAmbiguousFileIdentity(CoverageReport a, CoverageReport b, List<CoverageWarning> warnings)
    {
        if (!PathIdentity.RootsDiffer(a.SourceRoots, b.SourceRoots)) return;

        var aPaths = new HashSet<string>(a.Files.Select(static f => f.Path), StringComparer.Ordinal);
        var bPaths = new HashSet<string>(b.Files.Select(static f => f.Path), StringComparer.Ordinal);

        var bOnlyByName = b.Files.Select(static f => f.Path)
            .Where(p => !aPaths.Contains(p))
            .GroupBy(PathIdentity.FileNameOf, StringComparer.Ordinal)
            .ToDictionary(static g => g.Key, static g => g.ToList(), StringComparer.Ordinal);

        var aOnly = a.Files.Select(static f => f.Path).Where(p => !bPaths.Contains(p));
        foreach (var pa in aOnly)
        {
            if (!bOnlyByName.TryGetValue(PathIdentity.FileNameOf(pa), out var candidates)) continue;
            foreach (var pb in candidates)
                warnings.Add(new CoverageWarning(
                    CoverageWarningKind.FileIdentityAmbiguous,
                    pa,
                    0,
                    $"'{pa}' and '{pb}' share a file name under different source roots - if they are the same file, its coverage is double-counted in the merged totals"));
        }
    }
}

/// <summary>
/// Well-known exclusion patterns. Generated code, build output, migrations, and host bootstrap
/// files inflate coverage numbers.
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
        // fuse into the real file; coverlet's exclude-by-attribute setting for
        // CompilerGeneratedAttribute removes them at the source, which a path filter can't do.
        // Anchored to the exact filename: the bare substring "GlobalUsings" would also swallow
        // real product code in any directory whose *name* contains it (src/GlobalUsingsGenerator/…),
        // silently flipping a failing gate to PASS. The auto-generated coverlet artifact
        // ({Project}.GlobalUsings.g.cs under obj/) is already caught by ".g.cs" and "/obj/".
        "/GlobalUsings.cs",
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

    /// <summary>
    /// The one exclusion predicate, shared by <see cref="CoverageReport.Exclude(IEnumerable{string}, IEnumerable{string})"/>
    /// and <see cref="MethodCoverageReport.Exclude"/> so a path can never be excluded by one
    /// command and kept by another, whichever rule set (<see cref="WellKnown"/>,
    /// <see cref="TestCode"/>, or both) selected it. Case-insensitive substring match against
    /// the path with a virtual leading <c>/</c> (so leading-separator rules anchor at top
    /// level); a keep match always wins.
    /// </summary>
    internal static bool Excluded(string path, List<string> rules, List<string> keepRules)
    {
        var probe = '/' + path;   // virtual root: leading-separator rules anchor at top level
        return !keepRules.Any(k => probe.Contains(k, StringComparison.OrdinalIgnoreCase)) &&
               rules.Any(p => probe.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Split <paramref name="items"/> by <see cref="Excluded"/> on their file path: the kept items
    /// in order, and the distinct excluded paths in first-seen order.
    /// </summary>
    internal static (List<T> Kept, List<string> Excluded) Partition<T>(
        IEnumerable<T> items, Func<T, string> path, List<string> rules, List<string> keepRules)
    {
        var kept = new List<T>();
        var excluded = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            var file = path(item);
            if (!Excluded(file, rules, keepRules)) kept.Add(item);
            else if (seen.Add(file)) excluded.Add(file);
        }
        return (kept, excluded);
    }

    /// <summary>The ambiguous files an exclusion left in the report.</summary>
    internal static List<AmbiguousFile> Remaining(IReadOnlyList<AmbiguousFile> ambiguous, List<string> excluded)
    {
        var removed = new HashSet<string>(excluded, StringComparer.Ordinal);
        return ambiguous.Where(a => !removed.Contains(a.Path)).ToList();
    }
}

/// <summary>
/// What an exclusion did: the patterns applied, the <c>--keep</c> exemptions, and the files
/// removed. Carried on the report it produced, so every rendering — terminal, step summary,
/// JSON, upload — can say what was left out instead of presenting a smaller number as the
/// whole.
/// </summary>
/// <param name="Patterns">Exclusion patterns, distinct, in first-applied order.</param>
/// <param name="Keep">Keep exemptions, distinct, in first-applied order.</param>
/// <param name="ExcludedFiles">Paths removed and still absent from the report, distinct, in first-seen order.</param>
public sealed record FileExclusion(
    IReadOnlyList<string> Patterns,
    IReadOnlyList<string> Keep,
    IReadOnlyList<string> ExcludedFiles)
{
    /// <summary>No exclusion applied.</summary>
    public static FileExclusion None { get; } = new([], [], []);

    /// <summary>True once any exclusion pattern was applied, even one that removed nothing.</summary>
    public bool IsActive => Patterns.Count > 0;

    /// <summary>This exclusion followed by another.</summary>
    internal FileExclusion Then(List<string> patterns, List<string> keep, List<string> excluded) =>
        Union(new FileExclusion(patterns, keep, excluded));

    /// <summary>Both exclusions at once: every list unioned, Ordinal, first-seen order kept.</summary>
    internal FileExclusion Union(FileExclusion other) => new(
        Patterns.Concat(other.Patterns).Distinct(StringComparer.Ordinal).ToArray(),
        Keep.Concat(other.Keep).Distinct(StringComparer.Ordinal).ToArray(),
        ExcludedFiles.Concat(other.ExcludedFiles).Distinct(StringComparer.Ordinal).ToArray());
}
