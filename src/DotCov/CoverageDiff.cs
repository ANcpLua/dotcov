namespace DotCov;

public readonly record struct FileDelta(
    string Path,
    double? Before,
    double? After,
    double Delta,
    FileChangeKind Change)
{
    /// <summary>
    /// Codecov-style "indirect coverage changes": lines whose hit/miss state flipped
    /// between the two reports even though the line itself may not have appeared in the
    /// git diff. Surfaces removed-test / removed-import / dependency-change effects that
    /// the file-level <see cref="Delta"/> would otherwise smear into a single number.
    /// </summary>
    public IReadOnlyList<LineDelta> LineChanges { get; init; } = [];
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
    double beforeRate,
    double afterRate)
{
    public IReadOnlyList<FileDelta> Files { get; } = files;
    public double BeforeRate { get; } = beforeRate;
    public double AfterRate { get; } = afterRate;
    public double Delta => AfterRate - BeforeRate;

    public IEnumerable<FileDelta> Regressions => Files.Where(f => f.Delta < 0);
    public IEnumerable<FileDelta> Improvements => Files.Where(f => f.Delta > 0);
    public IEnumerable<FileDelta> Added => Files.Where(f => f.Change is FileChangeKind.Added);
    public IEnumerable<FileDelta> Removed => Files.Where(f => f.Change is FileChangeKind.Removed);

    /// <summary>
    /// Files that have at least one line whose hit/miss state flipped between the two
    /// reports. The set of indirectly-affected files — useful for surfacing "tests were
    /// removed / something broke upstream" in CI feedback.
    /// </summary>
    public IEnumerable<FileDelta> WithLineChanges => Files.Where(f => f.LineChanges.Count > 0);

    /// <summary>Total count of flipped lines across the whole report.</summary>
    public int TotalLineChanges => Files.Sum(f => f.LineChanges.Count);
}

public static class CoverageDiff
{
    /// <summary>
    /// Compare two reports. Detects added, removed, improved, and regressed files plus
    /// Codecov-style indirect line-level changes inside files that exist on both sides.
    /// </summary>
    public static CoverageDiffResult Compare(CoverageReport before, CoverageReport after)
    {
        var beforeLookup = before.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var afterLookup = after.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var allPaths = beforeLookup.Keys.Union(afterLookup.Keys, StringComparer.OrdinalIgnoreCase);

        // Union guarantees every path is in at least one lookup — the wildcard arm catches the
        // remaining `(false, true)` case, so no unreachable `_ => throw` arm is needed.
        var deltas = allPaths.Select(path =>
        {
            var hasBefore = beforeLookup.TryGetValue(path, out var b);
            var hasAfter = afterLookup.TryGetValue(path, out var a);

            return (hasBefore, hasAfter) switch
            {
                (true, true) => new FileDelta(path, b.LineRate, a.LineRate, a.LineRate - b.LineRate,
                    Math.Abs(a.LineRate - b.LineRate) < 0.0001 ? FileChangeKind.Unchanged : FileChangeKind.Modified)
                {
                    LineChanges = ComputeLineChanges(b, a)
                },
                (true, false) => new FileDelta(path, b.LineRate, null, -b.LineRate, FileChangeKind.Removed),
                _ => new FileDelta(path, null, a.LineRate, a.LineRate, FileChangeKind.Added)
            };
        })
        .OrderBy(d => d.Delta)
        .ToList();

        return new CoverageDiffResult(deltas, before.LineRate, after.LineRate);
    }

    private static IReadOnlyList<LineDelta> ComputeLineChanges(FileCoverage before, FileCoverage after)
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

            var beforeMissed = beforeHits == 0;
            var afterMissed = afterHits == 0;
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
