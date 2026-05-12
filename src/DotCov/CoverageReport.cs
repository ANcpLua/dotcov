namespace DotCov;

public readonly record struct BranchDetail(int Line, int Covered, int Total);

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

    public FileCoverage MergeWith(FileCoverage other)
    {
        var merged = new Dictionary<int, int>(LineHits);
        foreach (var (line, hits) in other.LineHits)
            merged[line] = merged.TryGetValue(line, out var existing) ? Math.Max(existing, hits) : hits;

        var linesHit = 0;
        var uncovered = new List<int>();
        foreach (var (line, hits) in merged)
        {
            if (hits > 0) linesHit++;
            else uncovered.Add(line);
        }
        uncovered.Sort();

        return new FileCoverage(
            Path,
            linesHit,
            merged.Count,
            BranchesHit + other.BranchesHit,
            BranchesTotal + other.BranchesTotal)
        {
            LineHits = merged,
            UncoveredLines = uncovered,
            PartialBranches = [.. PartialBranches, .. other.PartialBranches]
        };
    }
}

public sealed class CoverageReport
{
    public static readonly CoverageReport Empty = new([]);

    public CoverageReport(IReadOnlyList<FileCoverage> files) => Files = files;

    public IReadOnlyList<FileCoverage> Files { get; }

    public int TotalLines => Files.Sum(f => f.LinesTotal);
    public int TotalLinesHit => Files.Sum(f => f.LinesHit);
    public int TotalBranches => Files.Sum(f => f.BranchesTotal);
    public int TotalBranchesHit => Files.Sum(f => f.BranchesHit);

    public double LineRate => TotalLines is 0 ? 1.0 : (double)TotalLinesHit / TotalLines;
    public double BranchRate => TotalBranches is 0 ? 1.0 : (double)TotalBranchesHit / TotalBranches;

    /// <summary>True when any file in the report carries branch data.</summary>
    public bool HasBranchData => TotalBranches > 0;

    public IEnumerable<FileCoverage> BelowPercent(double linePercent) =>
        Files.Where(f => f.LineRate * 100 < linePercent);

    public bool MeetsThreshold(double minLinePercent, double minBranchPercent = 0) =>
        LineRate * 100 >= minLinePercent && BranchRate * 100 >= minBranchPercent;

    /// <summary>Remove files matching exclusion patterns. Returns a new report.</summary>
    public CoverageReport Exclude(IEnumerable<string> patterns)
    {
        var rules = patterns.ToList();
        if (rules.Count is 0) return this;

        var filtered = Files
            .Where(f => !rules.Any(p => f.Path.Contains(p, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return new CoverageReport(filtered);
    }

    public static CoverageReport Merge(CoverageReport a, CoverageReport b)
    {
        var merged = new Dictionary<string, FileCoverage>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in a.Files.Concat(b.Files))
        {
            merged[file.Path] = merged.TryGetValue(file.Path, out var existing)
                ? existing.MergeWith(file)
                : file;
        }

        return new CoverageReport(merged.Values.ToList());
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
    ];
}
