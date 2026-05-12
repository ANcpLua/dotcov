using DotCov.Tests.Infrastructure;
using Xunit;

namespace DotCov.Tests;

public sealed class ExclusionAndReportTests
{
    [Fact]
    public void WellKnown_ContainsExpectedPatterns()
    {
        Assert.Contains(".g.cs", ExclusionRules.WellKnown);
        Assert.Contains(".designer.cs", ExclusionRules.WellKnown);
        Assert.Contains("/obj/", ExclusionRules.WellKnown);
        Assert.Contains("/bin/", ExclusionRules.WellKnown);
        Assert.Contains("/Migrations/", ExclusionRules.WellKnown);
        Assert.Contains("d__", ExclusionRules.WellKnown);
        Assert.Contains("GlobalUsings", ExclusionRules.WellKnown);
    }

    [Fact]
    public void Exclude_EmptyPatterns_ReturnsSameInstance()
    {
        var report = Reports.Mixed;

        var filtered = report.Exclude([]);

        Assert.Same(report, filtered);
    }

    [Theory]
    [InlineData("src/MyApp/obj/Debug/file.cs", true)]
    [InlineData("src/MyApp/OBJ/Debug/file.cs", true)] // case-insensitive
    [InlineData("src/MyApp/bin/Release/file.cs", true)]
    [InlineData("Source/file.cs", false)]
    [InlineData("src/Foo.g.cs", true)]
    [InlineData("src/Form.Designer.cs", true)]
    [InlineData("src/MyApp/Migrations/20230101_Init.cs", true)]
    [InlineData("src/MyApp/<MyMethod>d__0.cs", true)]
    [InlineData("src/GlobalUsings.cs", true)]
    [InlineData("src/MyService.cs", false)]
    public void Exclude_WellKnown_FiltersByPattern(string path, bool shouldBeExcluded)
    {
        var report = new CoverageReport([
            new FileCoverage(path, 1, 1, 0, 0),
            new FileCoverage("KeepThis.cs", 1, 1, 0, 0)
        ]);

        var filtered = report.Exclude(ExclusionRules.WellKnown);

        if (shouldBeExcluded)
            Assert.DoesNotContain(filtered.Files, f => f.Path == path);
        else
            Assert.Contains(filtered.Files, f => f.Path == path);
    }

    [Fact]
    public void Exclude_DoesNotMutateSource()
    {
        var report = Reports.Mixed;
        var originalCount = report.Files.Count;

        _ = report.Exclude(["Unused"]);

        Assert.Equal(originalCount, report.Files.Count);
    }

    [Fact]
    public void Empty_StaticInstance_HasNoFilesAndPerfectRates()
    {
        Assert.Empty(CoverageReport.Empty.Files);
        Assert.Equal(1.0, CoverageReport.Empty.LineRate);
        Assert.Equal(1.0, CoverageReport.Empty.BranchRate);
        Assert.False(CoverageReport.Empty.HasBranchData);
    }

    [Fact]
    public void HasBranchData_FileWithBranches_True()
    {
        var file = new FileCoverage("a.cs", 1, 2, 1, 2);
        Assert.True(file.HasBranchData);
    }

    [Fact]
    public void HasBranchData_FileWithoutBranches_False()
    {
        var file = new FileCoverage("a.cs", 1, 2, 0, 0);
        Assert.False(file.HasBranchData);
    }

    [Fact]
    public void HasBranchData_ReportWithoutAnyBranches_False()
    {
        Assert.False(Reports.LinesOnly.HasBranchData);
        Assert.True(Reports.Mixed.HasBranchData);
    }

    [Fact]
    public void FileCoverage_MergeWith_UnionsLinesAndAppendsPartialBranches()
    {
        // `a` covers line 5, misses 10. `b` covers lines 20 and 30 (their hit count = 1),
        // misses 25. Merging keeps the highest hit count per line and recomputes the
        // uncovered-line list from the resulting union.
        var a = new FileCoverage("a.cs", LinesHit: 1, LinesTotal: 2, BranchesHit: 1, BranchesTotal: 2)
        {
            LineHits = new Dictionary<int, int> { [5] = 3, [10] = 0 },
            PartialBranches = [new BranchDetail(15, 1, 2)]
        };
        var b = new FileCoverage("a.cs", LinesHit: 2, LinesTotal: 3, BranchesHit: 1, BranchesTotal: 2)
        {
            LineHits = new Dictionary<int, int> { [20] = 1, [25] = 0, [30] = 1 },
            PartialBranches = [new BranchDetail(25, 0, 2)]
        };

        var merged = a.MergeWith(b);

        Assert.Equal([10, 25], merged.UncoveredLines);
        Assert.Equal(2, merged.PartialBranches.Count);
        Assert.Equal(3, merged.LinesHit);     // 5, 20, 30
        Assert.Equal(5, merged.LinesTotal);   // 5, 10, 20, 25, 30
        Assert.Equal(2, merged.BranchesHit);
    }

    [Fact]
    public void CoverageDiffResult_Improvements_FilterPositiveDeltas()
    {
        var diff = CoverageDiff.Compare(
            new CoverageReport([
                new FileCoverage("up.cs", 5, 10, 0, 0),
                new FileCoverage("down.cs", 9, 10, 0, 0)
            ]),
            new CoverageReport([
                new FileCoverage("up.cs", 9, 10, 0, 0),
                new FileCoverage("down.cs", 5, 10, 0, 0)
            ]));

        Assert.Single(diff.Improvements);
        Assert.Single(diff.Regressions);
        Assert.Equal("up.cs", diff.Improvements.Single().Path);
    }
}
