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
/// constructor is private — derivation is restricted to the four nested sealed records,
/// so consumers can pattern-match against the variants exhaustively. (Roslyn doesn't yet
/// prove this for switch-expressions; consumers use <c>is</c>-pattern chains with a final
/// unguarded cast to keep the code reachable while preserving the closed-set guarantee.)
/// </para>
/// </summary>
public abstract record LineDelta
{
    // Private constructor closes the hierarchy: derivation is restricted to the four
    // nested sealed records below. External types literally cannot inherit, so consumers
    // can rely on exhaustive pattern matches without a fallback arm.
    private LineDelta(int line) => Line = line;

    public int Line { get; }

    /// <summary>Line existed in After but not in Before (new code).</summary>
    public sealed record Added(int Line, int AfterHits) : LineDelta(Line);

    /// <summary>Line existed in Before but not in After (deleted code).</summary>
    public sealed record Removed(int Line, int BeforeHits) : LineDelta(Line);

    /// <summary>Same line in both reports; missed before, hit now (test added).</summary>
    public sealed record NewlyHit(int Line, int BeforeHits, int AfterHits) : LineDelta(Line);

    /// <summary>
    /// Same line in both reports; hit before, missed now (test removed or an upstream
    /// change stopped exercising it — the canonical Codecov "indirect change").
    /// </summary>
    public sealed record NewlyMissed(int Line, int BeforeHits, int AfterHits) : LineDelta(Line);
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
        // Walk the union of tracked line numbers; emit a LineDelta only when the
        // hit/miss state genuinely changed. We don't surface "still hit but hit count
        // dropped" — that's noisy and rarely actionable. Codecov's UI applies the same
        // hit-vs-miss boolean when classifying indirect changes.
        //
        // Structured as early-return guards (Added → Removed → state-flip) for readability;
        // the (false, false) combination is unreachable because allLines is the union.
        var allLines = before.LineHits.Keys.Union(after.LineHits.Keys);
        var changes = new List<LineDelta>();

        foreach (var line in allLines.OrderBy(x => x))
        {
            var hadBefore = before.LineHits.TryGetValue(line, out var beforeHits);
            var hadAfter = after.LineHits.TryGetValue(line, out var afterHits);

            if (!hadBefore)
            {
                changes.Add(new LineDelta.Added(line, afterHits));
                continue;
            }
            if (!hadAfter)
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

        return changes;
    }
}
