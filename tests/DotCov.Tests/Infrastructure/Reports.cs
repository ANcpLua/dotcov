namespace DotCov.Tests.Infrastructure;

/// <summary>Pre-built CoverageReport instances for formatter and gating tests.</summary>
public static class Reports
{
    public static CoverageReport Empty => CoverageReport.Empty;

    /// <summary>Three files: 100% covered, 60% covered, 0% covered. With branch data.</summary>
    public static CoverageReport Mixed => new([
        new FileCoverage("src/Calculator.cs", LinesHit: 4, LinesTotal: 4, BranchesHit: 2, BranchesTotal: 2),
        new FileCoverage("src/Parser.cs", LinesHit: 3, LinesTotal: 5, BranchesHit: 1, BranchesTotal: 4),
        new FileCoverage("src/Unused.cs", LinesHit: 0, LinesTotal: 3, BranchesHit: 0, BranchesTotal: 0)
    ]);

    /// <summary>Single file, lines only — mirrors MTP's default Cobertura emitter (no branch data).</summary>
    public static CoverageReport LinesOnly => new([
        new FileCoverage("src/App.cs", LinesHit: 410, LinesTotal: 769, BranchesHit: 0, BranchesTotal: 0)
    ]);

    /// <summary>Fully covered single file, with branches — used for "happy path" gating.</summary>
    public static CoverageReport FullyCovered => new([
        new FileCoverage("src/Perfect.cs", LinesHit: 10, LinesTotal: 10, BranchesHit: 4, BranchesTotal: 4)
    ]);

    public static CoverageReport Single(string path, int hit, int total, int bHit = 0, int bTotal = 0) =>
        new([new FileCoverage(path, hit, total, bHit, bTotal)]);

    /// <summary>
    /// Build a <see cref="FileCoverage"/> from raw line / branch dicts with the strict
    /// classification counts filled in via <see cref="FileCoverage.ClassifyLines"/>. The
    /// library's public surface intentionally exposes no factory — external callers do
    /// the same two-step dance — so this helper lives in test infrastructure to keep the
    /// fixtures readable without re-introducing the convenience method on the library.
    /// </summary>
    public static FileCoverage ClassifiedFile(
        string path,
        int linesHit,
        int linesTotal,
        int branchesHit,
        int branchesTotal,
        IReadOnlyDictionary<int, int>? lineHits = null,
        IReadOnlyDictionary<int, (int Covered, int Total)>? branchesByLine = null,
        IReadOnlyList<int>? uncoveredLines = null,
        IReadOnlyList<BranchDetail>? partialBranches = null)
    {
        var hits = lineHits ?? new Dictionary<int, int>();
        var branches = branchesByLine ?? new Dictionary<int, (int Covered, int Total)>();
        var (strict, partial) = FileCoverage.ClassifyLines(hits, branches);
        return new FileCoverage(path, linesHit, linesTotal, branchesHit, branchesTotal)
        {
            LineHits = hits,
            BranchesByLine = branches,
            UncoveredLines = uncoveredLines ?? [],
            PartialBranches = partialBranches ?? [],
            StrictlyHitLines = strict,
            PartiallyHitLines = partial
        };
    }
}
