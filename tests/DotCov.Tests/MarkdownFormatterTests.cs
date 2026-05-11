using DotCov.Formatters;
using DotCov.Tests.Infrastructure;
using Xunit;

namespace DotCov.Tests;

public sealed class MarkdownFormatterTests
{
    [Fact]
    public void Format_NoThreshold_OmitsStatusBadge()
    {
        var md = MarkdownFormatter.Format(Reports.Mixed);

        Assert.StartsWith("## Coverage Report\n", md.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Format_AboveThreshold_RendersPassEmoji()
    {
        var md = MarkdownFormatter.Format(Reports.FullyCovered, threshold: 80);

        Assert.Contains("## Coverage Report ✅", md);
    }

    [Fact]
    public void Format_BelowThreshold_RendersFailEmoji()
    {
        var md = MarkdownFormatter.Format(Reports.Mixed, threshold: 90);

        Assert.Contains("## Coverage Report ❌", md);
    }

    [Fact]
    public void Format_NoBranchData_RendersExplanatoryText()
    {
        var md = MarkdownFormatter.Format(Reports.LinesOnly);

        Assert.Contains("_no branch data emitted_", md);
        Assert.DoesNotContain("Branch coverage:** 100.0%", md);
    }

    [Fact]
    public void Format_WithBranchData_RendersBranchPercentage()
    {
        var md = MarkdownFormatter.Format(Reports.Mixed);

        Assert.Contains("Branch coverage:** 50.0%", md);
    }

    [Fact]
    public void Format_RowsSorted_ByLineRateAscending()
    {
        var md = MarkdownFormatter.Format(Reports.Mixed);

        var unusedIdx = md.IndexOf("Unused.cs", StringComparison.Ordinal);
        var parserIdx = md.IndexOf("Parser.cs", StringComparison.Ordinal);
        var calcIdx = md.IndexOf("Calculator.cs", StringComparison.Ordinal);

        Assert.True(unusedIdx < parserIdx && parserIdx < calcIdx);
    }

    [Fact]
    public void Format_FilesWithNoBranches_RenderDashesForBranchColumns()
    {
        var md = MarkdownFormatter.Format(Reports.LinesOnly);
        var fileRow = md.Split('\n').Single(l => l.Contains("App.cs"));

        // " - " appears in branches and branch % columns
        Assert.Matches(@"\|\s+-\s+\|\s+-\s+\|", fileRow);
    }

    [Fact]
    public void Format_RendersValidMarkdownTable()
    {
        var md = MarkdownFormatter.Format(Reports.Mixed);

        Assert.Contains("| File | Lines | Line % | Branches | Branch % |", md);
        Assert.Contains("|------|------:|-------:|---------:|---------:|", md);
    }

    [Fact]
    public void FormatDiff_NoChange_RendersRightArrowIcon()
    {
        var report = new CoverageReport([new FileCoverage("a.cs", 8, 10, 0, 0)]);
        var diff = CoverageDiff.Compare(report, report);

        var md = MarkdownFormatter.FormatDiff(diff);

        Assert.Contains("## Coverage Diff ➡️", md);
    }

    [Fact]
    public void FormatDiff_Improvement_RendersUpChart()
    {
        var diff = CoverageDiff.Compare(
            new CoverageReport([new FileCoverage("a.cs", 5, 10, 0, 0)]),
            new CoverageReport([new FileCoverage("a.cs", 8, 10, 0, 0)]));

        var md = MarkdownFormatter.FormatDiff(diff);

        Assert.Contains("## Coverage Diff 📈", md);
        Assert.Contains("+30.0%", md);
    }

    [Fact]
    public void FormatDiff_Regression_RendersDownChart()
    {
        var diff = CoverageDiff.Compare(
            new CoverageReport([new FileCoverage("a.cs", 9, 10, 0, 0)]),
            new CoverageReport([new FileCoverage("a.cs", 5, 10, 0, 0)]));

        var md = MarkdownFormatter.FormatDiff(diff);

        Assert.Contains("## Coverage Diff 📉", md);
    }

    [Fact]
    public void FormatDiff_AddedFile_BeforeIsDash()
    {
        var diff = CoverageDiff.Compare(
            CoverageReport.Empty,
            new CoverageReport([new FileCoverage("new.cs", 5, 10, 0, 0)]));

        var md = MarkdownFormatter.FormatDiff(diff);

        Assert.Matches(@"\|\s+`new\.cs`\s+\|\s+-\s+\|", md);
    }
}
