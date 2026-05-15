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

    [Fact]
    public void FormatDiff_RemovedFile_AfterIsDash()
    {
        var diff = CoverageDiff.Compare(
            new CoverageReport([new FileCoverage("gone.cs", 4, 5, 0, 0)]),
            CoverageReport.Empty);

        var md = MarkdownFormatter.FormatDiff(diff);

        Assert.Matches(@"\|\s+`gone\.cs`\s+\|\s+80\.0%\s+\|\s+-\s+\|", md);
    }

    [Fact]
    public void FormatDiff_IndirectLineChanges_RenderedAsSeparateSection()
    {
        // Same file on both sides; line 10 was hit, now missed → Codecov-style indirect change.
        var before = new CoverageReport([new FileCoverage("a.cs", 1, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 5 }
        }]);
        var after = new CoverageReport([new FileCoverage("a.cs", 0, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 0 }
        }]);

        var md = MarkdownFormatter.FormatDiff(CoverageDiff.Compare(before, after));

        Assert.Contains("### Indirect changes (1 line across 1 file)", md);
        Assert.Contains("1 newly missed", md);
        Assert.Contains("`a.cs`", md);
    }

    [Fact]
    public void FormatDiff_MultipleLinesAndFiles_HeadingUsesPluralForBoth()
    {
        var before = new CoverageReport([
            new FileCoverage("a.cs", 1, 1, 0, 0) { LineHits = new Dictionary<int, int> { [10] = 1 } },
            new FileCoverage("b.cs", 1, 1, 0, 0) { LineHits = new Dictionary<int, int> { [20] = 1 } }
        ]);
        var after = new CoverageReport([
            new FileCoverage("a.cs", 0, 1, 0, 0) { LineHits = new Dictionary<int, int> { [10] = 0 } },
            new FileCoverage("b.cs", 0, 1, 0, 0) { LineHits = new Dictionary<int, int> { [20] = 0 } }
        ]);

        var md = MarkdownFormatter.FormatDiff(CoverageDiff.Compare(before, after));

        Assert.Contains("### Indirect changes (2 lines across 2 files)", md);
    }

    [Fact]
    public void FormatDiff_NoIndirectChanges_OmitsSection()
    {
        var diff = CoverageDiff.Compare(
            new CoverageReport([new FileCoverage("a.cs", 5, 10, 0, 0)]),
            new CoverageReport([new FileCoverage("a.cs", 5, 10, 0, 0)]));

        var md = MarkdownFormatter.FormatDiff(diff);

        Assert.DoesNotContain("Indirect changes", md);
    }

    [Fact]
    public void FormatDiff_AllLineChangeKinds_RenderEachFragment()
    {
        // Exercises every fragment-add arm in AppendIndirectChanges: newlyMissed, newlyHit,
        // added, removed. The four-line file flips line 10 (hit→miss), line 20 (miss→hit),
        // drops line 30, and adds line 40 — one occurrence of each LineChangeKind.
        var before = new CoverageReport([new FileCoverage("a.cs", 2, 3, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 5, [20] = 0, [30] = 1 }
        }]);
        var after = new CoverageReport([new FileCoverage("a.cs", 2, 3, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 0, [20] = 3, [40] = 1 }
        }]);

        var md = MarkdownFormatter.FormatDiff(CoverageDiff.Compare(before, after));

        Assert.Contains("1 newly missed", md);
        Assert.Contains("1 newly hit", md);
        Assert.Contains("1 added", md);
        Assert.Contains("1 removed", md);
    }

    // ── Warnings section: additive — silent when empty, structured when populated ──

    [Fact]
    public void Format_NoWarnings_OmitsWarningsSection()
    {
        // Default reports should render identically to pre-warnings output; absence of
        // the `### Warnings` heading is the clean-report signal.
        var md = MarkdownFormatter.Format(Reports.Mixed);

        Assert.DoesNotContain("### Warnings", md);
    }

    [Fact]
    public void Format_WithWarnings_RendersHeadingAndEntries()
    {
        // Both warning kinds should round-trip with file:line context and the Detail
        // string. Pin the exact bullet shape so consumers parsing the markdown can rely
        // on it.
        var report = new CoverageReport([new FileCoverage("src/A.cs", 1, 1, 0, 0)])
        {
            Warnings =
            [
                new CoverageWarning(CoverageWarningKind.BranchTotalMismatch, "src/A.cs", 12,
                    "Total 5 vs 7 — keeping 7"),
                new CoverageWarning(CoverageWarningKind.MalformedConditionCoverage, "src/B.cs", 30,
                    "condition-coverage='???' could not be parsed")
            ]
        };

        var md = MarkdownFormatter.Format(report);

        Assert.Contains("### Warnings", md);
        Assert.Contains("- `src/A.cs:12` — BranchTotalMismatch: Total 5 vs 7 — keeping 7", md);
        Assert.Contains("- `src/B.cs:30` — MalformedConditionCoverage: condition-coverage='???' could not be parsed", md);
    }
}
