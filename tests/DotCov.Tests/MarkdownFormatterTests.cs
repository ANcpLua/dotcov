using DotCov.Formatters;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

public sealed class MarkdownFormatterTests
{
    [Test]
    public async Task Format_NoThreshold_OmitsStatusBadge()
    {
        var md = MarkdownFormatter.Format(Reports.Mixed);

        await Assert.That(md.ReplaceLineEndings("\n")).StartsWith("## Coverage Report\n");
    }

    [Test]
    public async Task Format_AboveThreshold_RendersPassEmoji()
    {
        var md = MarkdownFormatter.Format(Reports.FullyCovered, threshold: 80);

        await Assert.That(md).Contains("## Coverage Report ✅");
    }

    [Test]
    public async Task Format_BelowThreshold_RendersFailEmoji()
    {
        var md = MarkdownFormatter.Format(Reports.Mixed, threshold: 90);

        await Assert.That(md).Contains("## Coverage Report ❌");
    }

    [Test]
    public async Task Format_NoBranchData_RendersExplanatoryText()
    {
        var md = MarkdownFormatter.Format(Reports.LinesOnly);

        await Assert.That(md).Contains("_no branch data emitted_");
        await Assert.That(md).DoesNotContain("Branch coverage:** 100.0%");
    }

    [Test]
    public async Task Format_WithBranchData_RendersBranchPercentage()
    {
        var md = MarkdownFormatter.Format(Reports.Mixed);

        await Assert.That(md).Contains("Branch coverage:** 50.0%");
    }

    [Test]
    public async Task Format_RowsSorted_ByLineRateAscending()
    {
        var md = MarkdownFormatter.Format(Reports.Mixed);

        var unusedIdx = md.IndexOf("Unused.cs", StringComparison.Ordinal);
        var parserIdx = md.IndexOf("Parser.cs", StringComparison.Ordinal);
        var calcIdx = md.IndexOf("Calculator.cs", StringComparison.Ordinal);

        await Assert.That(unusedIdx < parserIdx && parserIdx < calcIdx).IsTrue();
    }

    [Test]
    public async Task Format_UnmeasuredFile_RendersFirst_BeforeZeroAndCoveredFiles()
    {
        // Shared worst-first contract with the table formatter: an unmeasured file (null
        // line rate, rendered "-") sorts ahead of even a 0%-covered file.
        var report = new CoverageReport([
            new FileCoverage("covered.cs", 1, 2, 0, 0),
            new FileCoverage("zero.cs", 0, 3, 0, 0),
            new FileCoverage("unmeasured.cs", 0, 0, 0, 0)
        ]);

        var md = MarkdownFormatter.Format(report);

        await Assert.That(md).Contains("| `unmeasured.cs` | 0/0 | - | - | - |");
        var unmeasuredIdx = md.IndexOf("`unmeasured.cs`", StringComparison.Ordinal);
        var zeroIdx = md.IndexOf("`zero.cs`", StringComparison.Ordinal);
        var coveredIdx = md.IndexOf("`covered.cs`", StringComparison.Ordinal);
        await Assert.That(unmeasuredIdx >= 0 && unmeasuredIdx < zeroIdx && zeroIdx < coveredIdx).IsTrue();
    }

    [Test]
    public async Task Format_FilesWithNoBranches_RenderDashesForBranchColumns()
    {
        var md = MarkdownFormatter.Format(Reports.LinesOnly);
        var fileRow = md.Split('\n').Single(l => l.Contains("App.cs"));

        // " - " appears in branches and branch % columns
        await Assert.That(fileRow).Matches(@"\|\s+-\s+\|\s+-\s+\|");
    }

    [Test]
    public async Task Format_FileWithBranches_PopulatesBranchColumns()
    {
        // The populated arm of the branch-column convention: exact row shape, so a file
        // that carries branch data can never regress to the no-data dash.
        var md = MarkdownFormatter.Format(Reports.Mixed);

        await Assert.That(md).Contains("| `src/Calculator.cs` | 4/4 | 100.0% | 2/2 | 100.0% |");
    }

    [Test]
    public async Task Format_RendersValidMarkdownTable()
    {
        var md = MarkdownFormatter.Format(Reports.Mixed);

        await Assert.That(md).Contains("| File | Lines | Line % | Branches | Branch % |");
        await Assert.That(md).Contains("|------|------:|-------:|---------:|---------:|");
    }

    [Test]
    public async Task FormatDiff_NoChange_RendersRightArrowIcon()
    {
        var report = new CoverageReport([new FileCoverage("a.cs", 8, 10, 0, 0)]);
        var diff = CoverageDiff.Compare(report, report);

        var md = MarkdownFormatter.FormatDiff(diff);

        await Assert.That(md).Contains("## Coverage Diff ➡️");
    }

    [Test]
    public async Task FormatDiff_Improvement_RendersUpChart()
    {
        var diff = CoverageDiff.Compare(
            new CoverageReport([new FileCoverage("a.cs", 5, 10, 0, 0)]),
            new CoverageReport([new FileCoverage("a.cs", 8, 10, 0, 0)]));

        var md = MarkdownFormatter.FormatDiff(diff);

        await Assert.That(md).Contains("## Coverage Diff 📈");
        await Assert.That(md).Contains("+30.0%");
    }

    [Test]
    public async Task FormatDiff_Regression_RendersDownChart()
    {
        var diff = CoverageDiff.Compare(
            new CoverageReport([new FileCoverage("a.cs", 9, 10, 0, 0)]),
            new CoverageReport([new FileCoverage("a.cs", 5, 10, 0, 0)]));

        var md = MarkdownFormatter.FormatDiff(diff);

        await Assert.That(md).Contains("## Coverage Diff 📉");
    }

    [Test]
    public async Task FormatDiff_AddedFile_BeforeIsDash()
    {
        var diff = CoverageDiff.Compare(
            CoverageReport.Empty,
            new CoverageReport([new FileCoverage("new.cs", 5, 10, 0, 0)]));

        var md = MarkdownFormatter.FormatDiff(diff);

        await Assert.That(md).Matches(@"\|\s+`new\.cs`\s+\|\s+-\s+\|");
    }

    [Test]
    public async Task FormatDiff_RemovedFile_AfterIsDash()
    {
        var diff = CoverageDiff.Compare(
            new CoverageReport([new FileCoverage("gone.cs", 4, 5, 0, 0)]),
            CoverageReport.Empty);

        var md = MarkdownFormatter.FormatDiff(diff);

        await Assert.That(md).Matches(@"\|\s+`gone\.cs`\s+\|\s+80\.0%\s+\|\s+-\s+\|");
    }

    [Test]
    public async Task FormatDiff_IndirectLineChanges_RenderedAsSeparateSection()
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

        await Assert.That(md).Contains("### Indirect changes (1 line across 1 file)");
        await Assert.That(md).Contains("1 newly missed");
        await Assert.That(md).Contains("`a.cs`");
    }

    [Test]
    public async Task FormatDiff_MultipleLinesAndFiles_HeadingUsesPluralForBoth()
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

        await Assert.That(md).Contains("### Indirect changes (2 lines across 2 files)");
    }

    [Test]
    public async Task FormatDiff_NoIndirectChanges_OmitsSection()
    {
        var diff = CoverageDiff.Compare(
            new CoverageReport([new FileCoverage("a.cs", 5, 10, 0, 0)]),
            new CoverageReport([new FileCoverage("a.cs", 5, 10, 0, 0)]));

        var md = MarkdownFormatter.FormatDiff(diff);

        await Assert.That(md).DoesNotContain("Indirect changes");
    }

    [Test]
    public async Task FormatDiff_AllLineDeltaVariants_RenderEachFragment()
    {
        // Exercises every fragment-add arm in AppendIndirectChanges: newlyMissed, newlyHit,
        // added, removed. The four-line file flips line 10 (hit→miss), line 20 (miss→hit),
        // drops line 30, and adds line 40 — one occurrence of each LineDelta variant.
        var before = new CoverageReport([new FileCoverage("a.cs", 2, 3, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 5, [20] = 0, [30] = 1 }
        }]);
        var after = new CoverageReport([new FileCoverage("a.cs", 2, 3, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 0, [20] = 3, [40] = 1 }
        }]);

        var md = MarkdownFormatter.FormatDiff(CoverageDiff.Compare(before, after));

        await Assert.That(md).Contains("1 newly missed");
        await Assert.That(md).Contains("1 newly hit");
        await Assert.That(md).Contains("1 added");
        await Assert.That(md).Contains("1 removed");
    }

    // ── Null-rate contract: unmeasured sides render "-", never a bare "%" ──

    [Test]
    public async Task FormatDiff_EmptyBefore_OverallRendersDashForUnmeasuredSides()
    {
        // Diff against an empty report is not a comparison: before and delta are null and
        // must render as "-" (table/JSON convention), not as "% → 58.3% (%)".
        var diff = CoverageDiff.Compare(
            CoverageReport.Empty,
            new CoverageReport([new FileCoverage("a.cs", 7, 12, 0, 0)]));

        var md = MarkdownFormatter.FormatDiff(diff);

        await Assert.That(md).Contains("**Overall:** - → 58.3% (-)");
    }

    [Test]
    public async Task FormatDiff_UnmeasuredFile_DeltaCellIsDashNotBareSign()
    {
        // A zero-line file has null rates on both sides, so its delta is null too — every
        // cell must follow the dash convention (the delta cell used to render "%").
        var diff = CoverageDiff.Compare(
            new CoverageReport([new FileCoverage("empty.cs", 0, 0, 0, 0)]),
            new CoverageReport([new FileCoverage("empty.cs", 0, 0, 0, 0)]));

        var md = MarkdownFormatter.FormatDiff(diff);

        await Assert.That(md).Contains("| `empty.cs` | - | - | - | Unchanged |");
    }

    // ── Warnings section: additive — silent when empty, structured when populated ──

    [Test]
    public async Task Format_NoWarnings_OmitsWarningsSection()
    {
        // Default reports should render identically to pre-warnings output; absence of
        // the `### Warnings` heading is the clean-report signal.
        var md = MarkdownFormatter.Format(Reports.Mixed);

        await Assert.That(md).DoesNotContain("### Warnings");
    }

    [Test]
    public async Task Format_WithWarnings_RendersHeadingAndEntries()
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

        await Assert.That(md).Contains("### Warnings");
        await Assert.That(md).Contains("- `src/A.cs:12` — BranchTotalMismatch: Total 5 vs 7 — keeping 7");
        await Assert.That(md).Contains("- `src/B.cs:30` — MalformedConditionCoverage: condition-coverage='???' could not be parsed");
    }
}