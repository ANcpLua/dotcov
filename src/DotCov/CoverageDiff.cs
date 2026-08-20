namespace DotCov;

public readonly record struct FileDelta(
    string Path,
    double? Before,
    double? After,
    double? Delta,
    FileChangeKind Change)
{
    /// <summary>
    /// Codecov-style "indirect coverage changes": lines whose hit/miss state flipped
    /// between the two reports even though the line itself may not have appeared in the
    /// git diff. Surfaces removed-test / removed-import / dependency-change effects that
    /// the file-level <see cref="Delta"/> would otherwise smear into a single number.
    /// </summary>
    /// <remarks>
    /// Null-coalesced getter so a <c>default(FileDelta)</c> (array growth, a dictionary
    /// lookup miss) yields an empty list instead of violating the non-nullable annotation.
    /// </remarks>
    public IReadOnlyList<LineDelta> LineChanges { get => field ?? []; init; } = [];
}

public enum FileChangeKind { Unchanged, Added, Removed, Modified }

/// <summary>
/// Per-line change between two coverage reports. Only emitted when the state actually
/// changed — equal-on-both-sides lines (same hit/miss boolean) are dropped so callers can
/// iterate the lists without filtering. Hit-count-still-hit transitions (100 → 1) are NOT
/// emitted — Codecov treats the hit/miss boolean as the change signal, not the magnitude.
/// <para>
/// Closed sealed-hierarchy: every variant carries exactly the data the diff actually has,
/// so illegal combinations (Added with BeforeHits, Removed with AfterHits, NewlyHit with a
/// missing AfterHits…) are unrepresentable rather than enforced by convention. The base
/// constructor is <c>private</c> — derivation is restricted to the four nested sealed
/// records below.
/// </para>
/// <para>
/// Consumers dispatch via the abstract <see cref="Match{T}"/> (returns a value) or
/// <see cref="Switch"/> (side-effecting) visitor methods. Adding a fifth variant requires
/// extending both signatures, which breaks every callsite at compile time — the actual
/// guarantee a closed sum type is supposed to give. No <c>_ => throw</c> fallback or
/// <c>is</c>-pattern chain is reachable on this surface.
/// </para>
/// </summary>
public abstract record LineDelta
{
    // Private constructor closes the hierarchy: derivation is restricted to the four
    // nested sealed records below. External types literally cannot inherit, so consumers
    // can rely on exhaustive Match/Switch dispatch with no fallback arm.
    private LineDelta(int line) => Line = line;

    public int Line { get; }

    /// <summary>
    /// Visitor dispatch over the closed variant set. Returns a value computed by the
    /// matching delegate. Adding a fifth variant breaks compile at every callsite — the
    /// desired property of a true sum type.
    /// </summary>
    public abstract T Match<T>(
        Func<Added, T> added,
        Func<Removed, T> removed,
        Func<NewlyHit, T> newlyHit,
        Func<NewlyMissed, T> newlyMissed);

    /// <summary>
    /// Side-effecting variant of <see cref="Match{T}"/>. Same compile-time exhaustiveness
    /// guarantee, useful for accumulators and fold-style counters.
    /// </summary>
    public abstract void Switch(
        Action<Added> added,
        Action<Removed> removed,
        Action<NewlyHit> newlyHit,
        Action<NewlyMissed> newlyMissed);

    /// <summary>Line existed in After but not in Before (new code).</summary>
    public sealed record Added(int Line, int AfterHits) : LineDelta(Line)
    {
        public override T Match<T>(Func<Added, T> added, Func<Removed, T> removed, Func<NewlyHit, T> newlyHit, Func<NewlyMissed, T> newlyMissed) => added(this);
        public override void Switch(Action<Added> added, Action<Removed> removed, Action<NewlyHit> newlyHit, Action<NewlyMissed> newlyMissed) => added(this);
    }

    /// <summary>Line existed in Before but not in After (deleted code).</summary>
    public sealed record Removed(int Line, int BeforeHits) : LineDelta(Line)
    {
        public override T Match<T>(Func<Added, T> added, Func<Removed, T> removed, Func<NewlyHit, T> newlyHit, Func<NewlyMissed, T> newlyMissed) => removed(this);
        public override void Switch(Action<Added> added, Action<Removed> removed, Action<NewlyHit> newlyHit, Action<NewlyMissed> newlyMissed) => removed(this);
    }

    /// <summary>Same line in both reports; missed before, hit now (test added).</summary>
    public sealed record NewlyHit(int Line, int BeforeHits, int AfterHits) : LineDelta(Line)
    {
        public override T Match<T>(Func<Added, T> added, Func<Removed, T> removed, Func<NewlyHit, T> newlyHit, Func<NewlyMissed, T> newlyMissed) => newlyHit(this);
        public override void Switch(Action<Added> added, Action<Removed> removed, Action<NewlyHit> newlyHit, Action<NewlyMissed> newlyMissed) => newlyHit(this);
    }

    /// <summary>
    /// Same line in both reports; hit before, missed now (test removed or an upstream
    /// change stopped exercising it — the canonical Codecov "indirect change").
    /// </summary>
    public sealed record NewlyMissed(int Line, int BeforeHits, int AfterHits) : LineDelta(Line)
    {
        public override T Match<T>(Func<Added, T> added, Func<Removed, T> removed, Func<NewlyHit, T> newlyHit, Func<NewlyMissed, T> newlyMissed) => newlyMissed(this);
        public override void Switch(Action<Added> added, Action<Removed> removed, Action<NewlyHit> newlyHit, Action<NewlyMissed> newlyMissed) => newlyMissed(this);
    }
}

/// <summary>
/// Result of comparing two coverage reports.
/// Single call instead of separate Compare + Summary — more cohesive.
/// </summary>
public sealed class CoverageDiffResult(
    IReadOnlyList<FileDelta> files,
    double? beforeRate,
    double? afterRate)
{
    public IReadOnlyList<FileDelta> Files { get; } = files;

    /// <summary>Aggregate line rate of the "before" report, or null when it carried no line data.</summary>
    public double? BeforeRate { get; } = beforeRate;

    /// <summary>Aggregate line rate of the "after" report, or null when it carried no line data.</summary>
    public double? AfterRate { get; } = afterRate;

    /// <summary>
    /// Movement between the two reports, or null when either side was unmeasured. A diff against
    /// an empty report is not a 100-point regression - it is not a comparison at all.
    /// </summary>
    public double? Delta => AfterRate - BeforeRate;

    /// <summary>
    /// Files whose rate fell. Derived from the <see cref="FileChangeKind"/> that
    /// <see cref="CoverageDiff.Compare"/> already computed, so a sub-epsilon wobble
    /// (classified <see cref="FileChangeKind.Unchanged"/>) can never simultaneously be
    /// "unchanged" here and "a regression" there. Removed files count — losing a measured
    /// file is a regression of what the report vouches for.
    /// </summary>
    public IEnumerable<FileDelta> Regressions =>
        Files.Where(static f => f.Change is not FileChangeKind.Unchanged && f.Delta < 0);

    /// <summary>Files whose rate rose — same single-classification contract as <see cref="Regressions"/>.</summary>
    public IEnumerable<FileDelta> Improvements =>
        Files.Where(static f => f.Change is not FileChangeKind.Unchanged && f.Delta > 0);
    public IEnumerable<FileDelta> Added => Files.Where(static f => f.Change is FileChangeKind.Added);
    public IEnumerable<FileDelta> Removed => Files.Where(static f => f.Change is FileChangeKind.Removed);

    /// <summary>
    /// Files that have at least one line whose hit/miss state flipped between the two
    /// reports. The set of indirectly-affected files — useful for surfacing "tests were
    /// removed / something broke upstream" in CI feedback.
    /// </summary>
    public IEnumerable<FileDelta> WithLineChanges => Files.Where(static f => f.LineChanges.Count > 0);

    /// <summary>Total count of flipped lines across the whole report.</summary>
    public int TotalLineChanges => Files.Sum(static f => f.LineChanges.Count);
}

public static class CoverageDiff
{
    /// <summary>
    /// The movement threshold shared by <see cref="Compare"/>'s Unchanged/Modified
    /// classification, <see cref="CoverageDiffResult.Regressions"/>/<see cref="CoverageDiffResult.Improvements"/>
    /// (via that classification), and <see cref="Formatters.AnsiPen.Delta"/>'s coloring:
    /// a rate delta closer to zero than this is measurement noise, not movement. One
    /// constant so the change kind, the regression list, and the rendered color can
    /// never disagree about whether coverage moved.
    /// </summary>
    public const double MovementEpsilon = 0.0001;

    /// <summary>
    /// Compare two reports. Detects added, removed, improved, and regressed files plus
    /// Codecov-style indirect line-level changes inside files that exist on both sides.
    /// <para>
    /// File matching is exact-path first (Ordinal — case-differing paths are distinct files
    /// on the case-sensitive filesystems Cobertura's native emitters run on), then falls back
    /// to pairing leftover files by unique file name: a file present only in Before pairs
    /// with a file present only in After when their final path segments match (whole-segment,
    /// Ordinal), the match is unambiguous on BOTH sides, and there is evidence of a
    /// path-convention change — the two reports declared different source roots (judged by
    /// <see cref="PathIdentity.RootsDiffer"/>, the same predicate the merge's ambiguity scan
    /// uses), the paths agree on a whole-segment suffix of at least two segments, or the
    /// paths are equal ignoring case (a directory-casing drift). That keeps a source-root
    /// migration, DeterministicSourcePaths turned on, or a pre-source-root snapshot diffed
    /// against a post-source-root report reading as the same file's movement instead of a
    /// full removed+added churn, while anything ambiguous (two candidates named
    /// <c>app/main.py</c>) or evidence-free (an unrelated <c>svc-b/Program.cs</c> appearing
    /// as <c>svc-a/Program.cs</c> disappears) honestly stays removed+added rather than being
    /// guessed together.
    /// </para>
    /// </summary>
    public static CoverageDiffResult Compare(CoverageReport before, CoverageReport after)
    {
        var beforeLookup = before.Files.ToDictionary(static f => f.Path, StringComparer.Ordinal);
        var afterLookup = after.Files.ToDictionary(static f => f.Path, StringComparer.Ordinal);
        var suffixPairs = PairByUniqueFileName(beforeLookup, afterLookup,
            PathIdentity.RootsDiffer(before.SourceRoots, after.SourceRoots));
        var pairedAfterPaths = new HashSet<string>(suffixPairs.Values, StringComparer.Ordinal);

        var deltas = new List<FileDelta>(beforeLookup.Count + afterLookup.Count);

        foreach (var (path, b) in beforeLookup)
        {
            if (afterLookup.TryGetValue(path, out var a))
                deltas.Add(Changed(path, b, a));
            else if (suffixPairs.TryGetValue(path, out var afterPath))
                // Suffix-paired: report under the After identity — that is the convention the
                // codebase now lives under.
                deltas.Add(Changed(afterPath, b, afterLookup[afterPath]));
            else
                deltas.Add(new FileDelta(path, b.LineRate, null, -b.LineRate, FileChangeKind.Removed));
        }

        foreach (var (path, a) in afterLookup)
        {
            if (beforeLookup.ContainsKey(path) || pairedAfterPaths.Contains(path)) continue;
            deltas.Add(new FileDelta(path, null, a.LineRate, a.LineRate, FileChangeKind.Added));
        }

        // OrderBy, not List.Sort: stable, so equal-delta files keep their encounter order
        // (Before files first, then After-only) — same ordering contract as before.
        return new CoverageDiffResult(
            deltas.OrderBy(static d => d.Delta).ToList(),
            before.LineRate,
            after.LineRate);

        static FileDelta Changed(string path, FileCoverage b, FileCoverage a) =>
            new(path, b.LineRate, a.LineRate, a.LineRate - b.LineRate,
                // A null delta means neither side carried line data: unmeasured on both ends is
                // unchanged, not modified.
                (a.LineRate - b.LineRate) is not { } d || Math.Abs(d) < MovementEpsilon
                    ? FileChangeKind.Unchanged
                    : FileChangeKind.Modified)
            {
                LineChanges = ComputeLineChanges(b, a)
            };
    }

    /// <summary>
    /// Pair Before-only paths with After-only paths by file name (final path segment,
    /// Ordinal). A pair forms only when it is unique on BOTH sides — one leftover Before
    /// file and one leftover After file carry that name. Whole-segment matching means
    /// <c>MyCalculator.cs</c> never pairs with <c>Calculator.cs</c> despite the raw string
    /// suffix, and any name carried by two leftover files on either side (the monorepo
    /// <c>svc-a/app/main.py</c> vs <c>svc-b/app/main.py</c> shape) pairs nothing.
    /// When the two reports' declared roots do NOT differ (<paramref name="rootsDiffer"/> —
    /// the merge-side predicate, so diff and merge agree on what a convention change is),
    /// uniqueness alone is not enough: the pair must also carry path evidence via
    /// <see cref="EvidencesSameFile"/>, because with equal roots a bare name collision is
    /// exactly as likely to be an unrelated deleted+added file pair.
    /// </summary>
    private static Dictionary<string, string> PairByUniqueFileName(
        Dictionary<string, FileCoverage> beforeLookup,
        Dictionary<string, FileCoverage> afterLookup,
        bool rootsDiffer)
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);

        var afterOnlyByName = afterLookup.Keys
            .Where(k => !beforeLookup.ContainsKey(k))
            .GroupBy(PathIdentity.FileNameOf, StringComparer.Ordinal)
            .ToDictionary(static g => g.Key, static g => g.ToList(), StringComparer.Ordinal);

        if (afterOnlyByName.Count is 0) return pairs;

        foreach (var group in beforeLookup.Keys
                     .Where(k => !afterLookup.ContainsKey(k))
                     .GroupBy(PathIdentity.FileNameOf, StringComparer.Ordinal))
        {
            var befores = group.ToList();
            if (befores.Count is not 1) continue;   // ambiguous on the Before side
            if (!afterOnlyByName.TryGetValue(group.Key, out var afters) || afters.Count is not 1)
                continue;                           // no match, or ambiguous on the After side
            if (!rootsDiffer && !EvidencesSameFile(befores[0], afters[0]))
                continue;                           // same roots, no path evidence: honestly distinct

            pairs[befores[0]] = afters[0];
        }

        return pairs;
    }

    /// <summary>
    /// Path evidence that two leftover same-named files are ONE file spelled under two
    /// conventions, consulted only when the reports' declared roots do not differ (differing
    /// roots are themselves the evidence). Either the paths agree on a whole-segment Ordinal
    /// suffix of at least two segments (a prefix migration: <c>src/MyApp/Calculator.cs</c> vs
    /// <c>/_/src/MyApp/Calculator.cs</c>) or they are equal ignoring case (a directory-casing
    /// drift: <c>SRC/App.cs</c> vs <c>src/App.cs</c> — never <c>xt_TCPMSS.c</c> vs
    /// <c>xt_tcpmss.c</c>, whose final segments already fail the Ordinal name grouping).
    /// A bare final-segment match is NOT evidence: <c>svc-a/Program.cs</c> deleted while
    /// <c>svc-b/Program.cs</c> appears would otherwise fuse into a fabricated Modified entry
    /// with line changes neither report contains.
    /// </summary>
    private static bool EvidencesSameFile(string before, string after)
    {
        if (string.Equals(before, after, StringComparison.OrdinalIgnoreCase)) return true;

        var b = before.Split('/');
        var a = after.Split('/');
        var limit = Math.Min(b.Length, a.Length);
        var agree = 0;
        while (agree < limit && string.Equals(b[^(agree + 1)], a[^(agree + 1)], StringComparison.Ordinal))
            agree++;
        return agree >= 2;
    }

    private static List<LineDelta> ComputeLineChanges(FileCoverage before, FileCoverage after)
    {
        // Probe the two hit dictionaries directly and only sort the emitted deltas. Large
        // reports usually have many stable lines and only a few real hit/miss flips, so
        // sorting the full union would spend O(n log n) work on lines that get discarded.
        var changes = new List<LineDelta>();

        foreach (var (line, beforeHits) in before.LineHits)
        {
            if (!after.LineHits.TryGetValue(line, out var afterHits))
            {
                changes.Add(new LineDelta.Removed(line, beforeHits));
                continue;
            }

            var beforeMissed = beforeHits is 0;
            var afterMissed = afterHits is 0;
            if (beforeMissed == afterMissed) continue;  // hit-state unchanged

            changes.Add(afterMissed
                ? new LineDelta.NewlyMissed(line, beforeHits, afterHits)
                : new LineDelta.NewlyHit(line, beforeHits, afterHits));
        }

        foreach (var (line, afterHits) in after.LineHits)
        {
            if (before.LineHits.ContainsKey(line)) continue;

            changes.Add(new LineDelta.Added(line, afterHits));
        }

        changes.Sort(static (left, right) => left.Line.CompareTo(right.Line));
        return changes;
    }
}
