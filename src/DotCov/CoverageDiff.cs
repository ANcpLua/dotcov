namespace DotCov;

public readonly record struct FileDelta(
    string Path,
    double? Before,
    double? After,
    double Delta,
    FileChangeKind Change);

public enum FileChangeKind { Unchanged, Added, Removed, Modified }

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
}

public static class CoverageDiff
{
    /// <summary>
    /// Compare two reports. Detects added, removed, improved, and regressed files.
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
                    Math.Abs(a.LineRate - b.LineRate) < 0.0001 ? FileChangeKind.Unchanged : FileChangeKind.Modified),
                (true, false) => new FileDelta(path, b.LineRate, null, -b.LineRate, FileChangeKind.Removed),
                _ => new FileDelta(path, null, a.LineRate, a.LineRate, FileChangeKind.Added)
            };
        })
        .OrderBy(d => d.Delta)
        .ToList();

        return new CoverageDiffResult(deltas, before.LineRate, after.LineRate);
    }
}
