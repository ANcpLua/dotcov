using Xunit;

namespace DotCov.Tests;

public sealed class CoverageDiffTests
{
    private static CoverageReport Make(params FileCoverage[] files) => new(files);

    [Fact]
    public void Compare_IdenticalReports_AllDeltasZero()
    {
        var report = Make(new FileCoverage("a.cs", 8, 10, 0, 0));
        var result = CoverageDiff.Compare(report, report);

        Assert.Single(result.Files);
        Assert.Equal(0.0, result.Files[0].Delta);
        Assert.Equal(FileChangeKind.Unchanged, result.Files[0].Change);
    }

    [Fact]
    public void Compare_ImprovedCoverage_PositiveDelta()
    {
        var before = Make(new FileCoverage("a.cs", 5, 10, 0, 0));
        var after = Make(new FileCoverage("a.cs", 8, 10, 0, 0));

        var result = CoverageDiff.Compare(before, after);

        Assert.Equal(0.3, result.Files[0].Delta, precision: 10);
        Assert.Equal(FileChangeKind.Modified, result.Files[0].Change);
    }

    [Fact]
    public void Compare_RegressionInCoverage_NegativeDelta()
    {
        var before = Make(new FileCoverage("a.cs", 9, 10, 0, 0));
        var after = Make(new FileCoverage("a.cs", 6, 10, 0, 0));

        var result = CoverageDiff.Compare(before, after);

        Assert.True(result.Files[0].Delta < 0);
        Assert.Single(result.Regressions);
    }

    [Fact]
    public void Compare_NewFileInAfter_MarkedAsAdded()
    {
        var before = Make();
        var after = Make(new FileCoverage("new.cs", 5, 10, 0, 0));

        var result = CoverageDiff.Compare(before, after);

        Assert.Single(result.Files);
        Assert.Null(result.Files[0].Before);
        Assert.Equal(FileChangeKind.Added, result.Files[0].Change);
        Assert.Single(result.Added);
    }

    [Fact]
    public void Compare_RemovedFile_MarkedAsRemoved()
    {
        var before = Make(new FileCoverage("old.cs", 8, 10, 0, 0));
        var after = Make();

        var result = CoverageDiff.Compare(before, after);

        Assert.Single(result.Files);
        Assert.Null(result.Files[0].After);
        Assert.Equal(FileChangeKind.Removed, result.Files[0].Change);
        Assert.Single(result.Removed);
    }

    [Fact]
    public void Compare_Summary_ReportsOverallDelta()
    {
        var before = Make(new FileCoverage("a.cs", 5, 10, 0, 0));
        var after = Make(new FileCoverage("a.cs", 8, 10, 0, 0));

        var result = CoverageDiff.Compare(before, after);

        Assert.Equal(0.5, result.BeforeRate);
        Assert.Equal(0.8, result.AfterRate);
        Assert.Equal(0.3, result.Delta, precision: 10);
    }

    [Fact]
    public void Compare_OrdersByDeltaAscending_WorstFirst()
    {
        var before = Make(
            new FileCoverage("good.cs", 5, 10, 0, 0),
            new FileCoverage("bad.cs", 9, 10, 0, 0));
        var after = Make(
            new FileCoverage("good.cs", 9, 10, 0, 0),
            new FileCoverage("bad.cs", 3, 10, 0, 0));

        var result = CoverageDiff.Compare(before, after);

        Assert.Equal("bad.cs", result.Files[0].Path);
        Assert.Equal("good.cs", result.Files[1].Path);
    }

    [Fact]
    public void Compare_CaseInsensitivePaths_MatchesCorrectly()
    {
        var before = Make(new FileCoverage("SRC/App.cs", 5, 10, 0, 0));
        var after = Make(new FileCoverage("src/App.cs", 8, 10, 0, 0));

        var result = CoverageDiff.Compare(before, after);

        Assert.Single(result.Files);
        Assert.NotNull(result.Files[0].Before);
    }
}
