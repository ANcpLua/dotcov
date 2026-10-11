namespace DotCov.Tests.Infrastructure;

/// <summary>Pre-built CoverageReport instances for formatter and gating tests.</summary>
public static class Reports
{
    public static CoverageReport Empty => CoverageReport.Empty;

    public static CoverageReport Mixed => new([
        new FileCoverage("src/Calculator.cs", LinesHit: 4, LinesTotal: 4, BranchesHit: 2, BranchesTotal: 2),
        new FileCoverage("src/Parser.cs", LinesHit: 3, LinesTotal: 5, BranchesHit: 1, BranchesTotal: 4),
        new FileCoverage("src/Unused.cs", LinesHit: 0, LinesTotal: 3, BranchesHit: 0, BranchesTotal: 0)
    ]);

    /// <summary>Single file, lines only — mirrors MTP's default Cobertura emitter (no branch data).</summary>
    public static CoverageReport LinesOnly => new([
        new FileCoverage("src/App.cs", LinesHit: 410, LinesTotal: 769, BranchesHit: 0, BranchesTotal: 0)
    ]);

    public static CoverageReport FullyCovered => new([
        new FileCoverage("src/Perfect.cs", LinesHit: 10, LinesTotal: 10, BranchesHit: 4, BranchesTotal: 4)
    ]);

    public static CoverageReport Single(string path, int hit, int total, int bHit = 0, int bTotal = 0) =>
        new([new FileCoverage(path, hit, total, bHit, bTotal)]);

    /// <summary>
    /// Build a <see cref="FileCoverage"/> from raw line and branch dictionaries, with the strict
    /// classification counts filled in through <see cref="FileCoverage.ClassifyLines"/>. The
    /// library deliberately exposes no such factory; external callers take the same two steps.
    /// </summary>
    public static FileCoverage ClassifiedFile(
        string path,
        int linesHit,
        int linesTotal,
        int branchesHit,
        int branchesTotal,
        IReadOnlyDictionary<int, int>? lineHits = null,
        IReadOnlyDictionary<int, (int Covered, int Total)>? branchesByLine = null)
    {
        var hits = lineHits ?? new Dictionary<int, int>();
        var branches = branchesByLine ?? new Dictionary<int, (int Covered, int Total)>();
        var (strict, partial) = FileCoverage.ClassifyLines(hits, branches);
        return new FileCoverage(path, linesHit, linesTotal, branchesHit, branchesTotal)
        {
            LineHits = hits,
            BranchesByLine = branches,
            StrictlyHitLines = strict,
            PartiallyHitLines = partial
        };
    }
}
