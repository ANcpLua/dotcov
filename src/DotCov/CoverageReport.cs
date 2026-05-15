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
    /// one CI job built Release, the other Debug). <see cref="FileCoverage.MergeWithWarnings"/>
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
    public double LineRate => LinesTotal is 0 ? 1.0 : (double)LinesHit / LinesTotal;
    public double BranchRate => BranchesTotal is 0 ? 1.0 : (double)BranchesHit / BranchesTotal;

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
    /// Debug) — the merge keeps the larger total without flagging the divergence.
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<int, (int Covered, int Total)> BranchesByLine { get; init; } =
        new Dictionary<int, (int Covered, int Total)>();

    /// <summary>
    /// Codecov-style three-state classification for a single source line. Returns
    /// <see cref="LineStatus.Miss"/> for unknown lines so callers can iterate over any line
    /// range without first checking <see cref="LineHits"/> membership — note this conflates
    /// "tracked but zero hits" with "not tracked at all"; pre-filter via
    /// <c>LineHits.ContainsKey(line)</c> if the distinction matters.
    /// </summary>
    public LineStatus GetLineStatus(int line)
    {
        if (!LineHits.TryGetValue(line, out var hits) || hits == 0)
            return LineStatus.Miss;
        if (BranchesByLine.TryGetValue(line, out var b) && b.Covered < b.Total)
            return LineStatus.Partial;
        return LineStatus.Hit;
    }

    /// <summary>Lines that were executed AND had all their branches exercised.</summary>
    public int StrictlyHitLines
    {
        get
        {
            var count = 0;
            foreach (var line in LineHits.Keys)
                if (GetLineStatus(line) is LineStatus.Hit) count++;
            return count;
        }
    }

    /// <summary>Lines that were executed but had at least one unexercised branch.</summary>
    public int PartiallyHitLines
    {
        get
        {
            var count = 0;
            foreach (var line in LineHits.Keys)
                if (GetLineStatus(line) is LineStatus.Partial) count++;
            return count;
        }
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
    public double StrictLineRate => LinesTotal is 0 ? 1.0 : (double)StrictlyHitLines / LinesTotal;

    public FileCoverage MergeWith(FileCoverage other) => MergeWithWarnings(other).Merged;

    /// <summary>
    /// Merge semantics identical to <see cref="MergeWith"/>, plus a structured warnings
    /// list for anomalies the silent <c>Math.Max</c> reconciliation would otherwise hide.
    /// Currently emits one <see cref="CoverageWarningKind.BranchTotalMismatch"/> per line
    /// whose <c>Total</c> disagrees between the two sides — usually a sign that the
    /// reports came from different compile targets (e.g. Release vs. Debug builds in
    /// different CI jobs). Use this overload when you want the divergence observable;
    /// <see cref="MergeWith"/> stays as the lossy convenience for callers that don't care.
    /// </summary>
    public (FileCoverage Merged, IReadOnlyList<CoverageWarning> Warnings) MergeWithWarnings(FileCoverage other)
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
        foreach (var (line, b) in other.BranchesByLine)
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
        foreach (var (line, b) in mergedBranches.OrderBy(kv => kv.Key))
        {
            branchesHit += b.Covered;
            branchesTotal += b.Total;
            if (b.Covered < b.Total)
                partialBranches.Add(new BranchDetail(line, b.Covered, b.Total));
        }

        var merged = new FileCoverage(Path, linesHit, mergedHits.Count, branchesHit, branchesTotal)
        {
            LineHits = mergedHits,
            BranchesByLine = mergedBranches,
            UncoveredLines = uncovered,
            PartialBranches = partialBranches
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

    public int TotalLines => Files.Sum(f => f.LinesTotal);
    public int TotalLinesHit => Files.Sum(f => f.LinesHit);
    public int TotalBranches => Files.Sum(f => f.BranchesTotal);
    public int TotalBranchesHit => Files.Sum(f => f.BranchesHit);

    public double LineRate => TotalLines is 0 ? 1.0 : (double)TotalLinesHit / TotalLines;
    public double BranchRate => TotalBranches is 0 ? 1.0 : (double)TotalBranchesHit / TotalBranches;

    /// <summary>True when any file in the report carries branch data.</summary>
    public bool HasBranchData => TotalBranches > 0;

    /// <summary>
    /// Codecov-style strict ratio across the whole report. A line counts only when its
    /// branches are all exercised — partials and misses both depress the rate.
    /// </summary>
    public double StrictLineRate
    {
        get
        {
            if (TotalLines is 0) return 1.0;
            var strictHit = 0;
            foreach (var f in Files) strictHit += f.StrictlyHitLines;
            return (double)strictHit / TotalLines;
        }
    }

    public IEnumerable<FileCoverage> BelowPercent(double linePercent) =>
        Files.Where(f => f.LineRate * 100 < linePercent);

    public bool MeetsThreshold(double minLinePercent, double minBranchPercent = 0) =>
        LineRate * 100 >= minLinePercent && BranchRate * 100 >= minBranchPercent;

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
                var (mergedFile, mergeWarnings) = existing.MergeWithWarnings(file);
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
        "d__",            // async state machine classes (e.g. +<MethodName>d__0)
        "GlobalUsings",   // auto-generated global usings
        "/Program.cs",    // ASP.NET Core / generic host bootstrap — opt back in with --keep Program.cs
    ];
}
