using DotCov.Formatters;
using DotCov.Tests.Infrastructure;
using Xunit;

namespace DotCov.Tests;

public sealed class TableFormatterTests
{
    [Fact]
    public void Format_DefaultPlain_ContainsNoAnsiEscapes()
    {
        var output = TableFormatter.Format(Reports.Mixed);

        Assert.False(AnsiStrip.ContainsAnsi(output));
    }

    [Fact]
    public void Format_ExplicitColorOff_ContainsNoAnsiEscapes()
    {
        var output = TableFormatter.Format(Reports.Mixed, color: false);

        Assert.False(AnsiStrip.ContainsAnsi(output));
    }

    [Fact]
    public void Format_ColorEnabled_EmitsAnsiEscapes()
    {
        var output = TableFormatter.Format(Reports.Mixed, color: true);

        Assert.True(AnsiStrip.ContainsAnsi(output));
    }

    [Fact]
    public void Format_StrippedColoredOutput_MatchesPlainOutput()
    {
        var plain = TableFormatter.Format(Reports.Mixed, color: false);
        var colored = TableFormatter.Format(Reports.Mixed, color: true);

        Assert.Equal(plain, AnsiStrip.From(colored));
    }

    [Fact]
    public void Format_RowsOrderedByLineRateAscending()
    {
        var output = TableFormatter.Format(Reports.Mixed);
        var lines = output.Split('\n');

        var unusedRow = Array.FindIndex(lines, l => l.Contains("Unused.cs"));
        var parserRow = Array.FindIndex(lines, l => l.Contains("Parser.cs"));
        var calcRow = Array.FindIndex(lines, l => l.Contains("Calculator.cs"));

        Assert.True(unusedRow < parserRow && parserRow < calcRow,
            "Files should be sorted worst-to-best by line rate");
    }

    [Fact]
    public void Format_NoBranchData_RendersDashInsteadOfHundredPercent()
    {
        var output = TableFormatter.Format(Reports.LinesOnly);

        Assert.DoesNotContain("100.0%", output);
        Assert.Matches(@"\s-\s", output);
    }

    [Fact]
    public void Format_TotalRow_ContainsCorrectAggregateNumbers()
    {
        var output = TableFormatter.Format(Reports.Mixed);

        Assert.Contains("TOTAL", output);
        Assert.Contains("7/12", output); // 4+3+0 hit / 4+5+3 total
    }

    [Fact]
    public void Format_EmptyReport_RendersHeaderAndTotalOnly()
    {
        var output = TableFormatter.Format(Reports.Empty);

        Assert.Contains("File", output);
        Assert.Contains("TOTAL", output);
        Assert.Contains("0/0", output);
    }

    [Fact]
    public void Format_ColorEnabled_BoldsTotalRow()
    {
        var output = TableFormatter.Format(Reports.Mixed, color: true);
        var totalLine = output.Split('\n').Single(l => l.Contains("TOTAL"));

        Assert.Contains("\x1b[1m", totalLine);
    }

    [Fact]
    public void Format_ColorEnabled_PaintsCyanBoldHeader()
    {
        var output = TableFormatter.Format(Reports.Mixed, color: true);
        var headerLine = output.Split('\n')[0];

        Assert.Contains("\x1b[36m", headerLine);
        Assert.Contains("\x1b[1m", headerLine);
    }

    [Fact]
    public void FormatDiff_DefaultPlain_NoAnsi()
    {
        var diff = CoverageDiff.Compare(
            Reports.Single("a.cs", hit: 5, total: 10),
            Reports.Single("a.cs", hit: 8, total: 10));

        Assert.False(AnsiStrip.ContainsAnsi(TableFormatter.FormatDiff(diff)));
    }

    [Fact]
    public void FormatDiff_ColorEnabled_PaintsDeltaByDirection()
    {
        var diff = CoverageDiff.Compare(
            new CoverageReport([new FileCoverage("regress.cs", 9, 10, 0, 0)]),
            new CoverageReport([new FileCoverage("regress.cs", 6, 10, 0, 0)]));

        var output = TableFormatter.FormatDiff(diff, color: true);

        Assert.Contains("\x1b[31m", output); // red for the negative delta
    }

    [Fact]
    public void FormatDiff_ColorEnabled_PaintsAddedGreen()
    {
        var diff = CoverageDiff.Compare(
            CoverageReport.Empty,
            new CoverageReport([new FileCoverage("new.cs", 5, 10, 0, 0)]));

        var output = TableFormatter.FormatDiff(diff, color: true);

        Assert.Contains("\x1b[32m", output);
        Assert.Contains("Added", output);
    }

    [Fact]
    public void Format_PathColumnWidth_AdjustsToLongestPath()
    {
        var report = new CoverageReport([
            new FileCoverage("short.cs", 1, 1, 0, 0),
            new FileCoverage("very/long/nested/path/to/some/source.cs", 1, 1, 0, 0)
        ]);

        var output = TableFormatter.Format(report);
        var lines = output.Split('\n');

        Assert.Contains("very/long/nested/path/to/some/source.cs", output);
        var dividerLength = lines.First(l => l.StartsWith("-")).Length;
        Assert.True(dividerLength > "very/long/nested/path/to/some/source.cs".Length);
    }
}
