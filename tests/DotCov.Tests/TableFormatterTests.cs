using DotCov.Formatters;
using DotCov.Tests.Infrastructure;
using Xunit;

namespace DotCov.Tests;

public sealed class TableFormatterTests
{
    private static readonly AnsiPen Pen = new(enabled: true);

    // Derives a style's opening escape from AnsiPen itself, so these tests assert
    // "this cell is styled by pen X" without re-pinning the byte-level codes —
    // those are pinned exactly once, in AnsiPenTests.
    private static string Open(Func<string, string> style) => style("\0").Split('\0')[0];

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
        var row = output.Split('\n').Single(l => l.Contains("App.cs")).TrimEnd();

        // Anchored to the file's own row: line columns populated, both branch columns dashed.
        Assert.Matches(@"^src/App\.cs\s+410/769\s+53\.3%\s+-\s+-$", row);
    }

    [Fact]
    public void Format_TotalRow_ContainsCorrectAggregateNumbers()
    {
        var output = TableFormatter.Format(Reports.Mixed);

        Assert.Contains("TOTAL", output);
        Assert.Contains("7/12", output); // 4+3+0 hit / 4+5+3 total
    }

    [Fact]
    public void Format_FileWithBranches_PopulatesBranchCell()
    {
        // The populated arm of the branch-cell convention: a file that carries branch data
        // renders hit/total, never the no-data dash.
        var output = TableFormatter.Format(Reports.Mixed);
        var calcRow = output.Split('\n').Single(l => l.Contains("Calculator.cs"));

        Assert.Contains("2/2", calcRow);
    }

    [Fact]
    public void Format_TotalRow_PopulatesAggregateBranchCell()
    {
        // TOTAL branch cell is the branch aggregate (2+1+0)/(2+4+0), not the lines cell.
        var output = TableFormatter.Format(Reports.Mixed);
        var totalRow = output.Split('\n').Single(l => l.Contains("TOTAL"));

        Assert.Contains("3/6", totalRow);
    }

    [Fact]
    public void Format_NoBranchData_TotalRowRendersDashInBranchColumns()
    {
        var output = TableFormatter.Format(Reports.LinesOnly);
        var totalRow = output.Split('\n').Single(l => l.Contains("TOTAL")).TrimEnd();

        // Anchored to the TOTAL row itself: line columns populated, both branch columns dashed.
        Assert.Matches(@"^TOTAL\s+410/769\s+53\.3%\s+-\s+-$", totalRow);
    }

    [Fact]
    public void Format_ColorEnabled_PaintsBranchCellsByBranchRate()
    {
        // Branch cells are painted by pen.Rate over the branch rate — per-file
        // (Calculator.cs: 2/2 → green) and TOTAL (3/6 = 50% → yellow, bold).
        var output = TableFormatter.Format(Reports.Mixed, color: true);

        // Asserted as the adjacent branches+branch% cell pair so the line-% cell (also
        // green "  100.0%" for Calculator.cs) cannot satisfy the branch-cell assertion.
        Assert.Contains($"{Pen.Rate($"{"2/2",10}", 1.0)}  {Pen.Rate("  100.0%", 1.0)}", output);
        Assert.Contains($"{Pen.Bold(Pen.Rate($"{"3/6",10}", 0.5))}  {Pen.Bold(Pen.Rate("   50.0%", 0.5))}", output);
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

        Assert.Contains(Open(Pen.Bold), totalLine);
    }

    [Fact]
    public void Format_ColorEnabled_PaintsCyanBoldHeader()
    {
        var output = TableFormatter.Format(Reports.Mixed, color: true);
        var headerLine = output.Split('\n')[0].TrimEnd();

        // The header is exactly its plain text run through Bold(Cyan(…)) — rebuilt from
        // an AnsiPen so a palette change in the pen cannot break a formatter test.
        Assert.Equal(Pen.Bold(Pen.Cyan(AnsiStrip.From(headerLine))), headerLine);
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

        // The regression's delta cell is painted by the pen's delta rule (red arm).
        // Cell is the full 8-char unit — sign attached to the digits, then right-aligned.
        Assert.Contains(Pen.Delta("  -30.0%", diff.Delta), output);
    }

    [Fact]
    public void FormatDiff_ColorEnabled_PaintsAddedGreen()
    {
        var diff = CoverageDiff.Compare(
            CoverageReport.Empty,
            new CoverageReport([new FileCoverage("new.cs", 5, 10, 0, 0)]));

        var output = TableFormatter.FormatDiff(diff, color: true);

        Assert.Contains(Pen.Green($"{FileChangeKind.Added,10}"), output);
    }

    [Fact]
    public void FormatDiff_RemovedFile_BeforeShownAfterDashed()
    {
        var diff = CoverageDiff.Compare(
            new CoverageReport([new FileCoverage("gone.cs", 4, 5, 0, 0)]),
            CoverageReport.Empty);

        var output = TableFormatter.FormatDiff(diff, color: true);
        var row = AnsiStrip.From(output).Split('\n').Single(l => l.Contains("gone.cs")).TrimEnd();

        // Before populated, after dashed, delta -before — pinned on the file's own row so
        // a dash elsewhere (TOTAL row, other columns) cannot satisfy the assertion.
        Assert.Matches(@"^gone\.cs\s+80\.0%\s+-\s+-80\.0%\s+Removed$", row);
    }

    [Fact]
    public void FormatDiff_AddedFile_BeforeDashedAfterShown()
    {
        var diff = CoverageDiff.Compare(
            CoverageReport.Empty,
            new CoverageReport([new FileCoverage("fresh.cs", 7, 10, 0, 0)]));

        var output = TableFormatter.FormatDiff(diff);
        var row = output.Split('\n').Single(l => l.Contains("fresh.cs")).TrimEnd();

        // The '+' sign is attached to its digits — never a detached "+  70.0%".
        Assert.Matches(@"^fresh\.cs\s+-\s+70\.0%\s+\+70\.0%\s+Added$", row);
    }

    // ── Delta cell policy: sign attached to digits, one 8-char unit, rows and TOTAL alike ──

    [Fact]
    public void FormatDiff_TotalRow_RendersAggregateDeltaWithAttachedSign()
    {
        // Multi-file diff whose TOTAL delta (+25.0%) differs from every per-file delta
        // (+30.0%, +20.0%), so this pins the TOTAL arithmetic itself — a whole-output
        // Contains would otherwise be satisfied by a per-file cell.
        var diff = CoverageDiff.Compare(
            new CoverageReport([
                new FileCoverage("a.cs", 5, 10, 0, 0),
                new FileCoverage("b.cs", 5, 10, 0, 0)
            ]),
            new CoverageReport([
                new FileCoverage("a.cs", 8, 10, 0, 0),
                new FileCoverage("b.cs", 7, 10, 0, 0)
            ]));

        var output = TableFormatter.FormatDiff(diff);
        var totalRow = output.Split('\n').Single(l => l.Contains("TOTAL")).TrimEnd();

        Assert.Matches(@"^TOTAL\s+50\.0%\s+75\.0%\s+\+25\.0%$", totalRow);
        Assert.Contains("  +25.0%", totalRow); // full 8-char cell: sign never detaches
    }

    [Fact]
    public void FormatDiff_ZeroDelta_RowAndTotalUseSamePlusZeroCell()
    {
        // One sign policy everywhere: zero renders '+0.0%' in per-file rows and TOTAL alike
        // (previously the row showed '    0.0%' while TOTAL showed '+   0.0%').
        var diff = CoverageDiff.Compare(
            Reports.Single("same.cs", hit: 5, total: 10),
            Reports.Single("same.cs", hit: 5, total: 10));

        var output = TableFormatter.FormatDiff(diff);
        var fileRow = output.Split('\n').Single(l => l.Contains("same.cs")).TrimEnd();
        var totalRow = output.Split('\n').Single(l => l.Contains("TOTAL")).TrimEnd();

        Assert.Contains("   +0.0%", fileRow);
        Assert.Contains("   +0.0%", totalRow);
    }

    [Fact]
    public void FormatDiff_NegativeDelta_CellIsEightCharsWithOwnMinusSign()
    {
        var diff = CoverageDiff.Compare(
            Reports.Single("down.cs", hit: 9, total: 10),
            Reports.Single("down.cs", hit: 6, total: 10));

        var output = TableFormatter.FormatDiff(diff);
        var fileRow = output.Split('\n').Single(l => l.Contains("down.cs")).TrimEnd();
        var totalRow = output.Split('\n').Single(l => l.Contains("TOTAL")).TrimEnd();

        // Negative cells align with every other delta state: 8 chars, no '+' anywhere.
        Assert.Contains("  -30.0%", fileRow);
        Assert.Contains("  -30.0%", totalRow);
        Assert.DoesNotContain("+", fileRow);
    }

    [Fact]
    public void FormatDiff_NullDelta_RendersDashCell()
    {
        // A 0/0 file has null rates on both sides → null delta → the 8-char dash cell.
        var diff = CoverageDiff.Compare(
            new CoverageReport([new FileCoverage("empty.cs", 0, 0, 0, 0)]),
            new CoverageReport([new FileCoverage("empty.cs", 0, 0, 0, 0)]));

        var output = TableFormatter.FormatDiff(diff);
        var fileRow = output.Split('\n').Single(l => l.Contains("empty.cs")).TrimEnd();

        Assert.Matches(@"^empty\.cs\s+-\s+-\s+-\s+Unchanged$", fileRow);
        Assert.Contains("       -", fileRow); // dash keeps the 8-char cell width
    }

    [Fact]
    public void FormatDiff_UnchangedFile_NeutralIndicator()
    {
        var diff = CoverageDiff.Compare(
            Reports.Single("same.cs", hit: 5, total: 10),
            Reports.Single("same.cs", hit: 5, total: 10));

        var output = TableFormatter.FormatDiff(diff, color: true);

        // The unchanged file's Change cell is dimmed, not painted with a verdict color.
        Assert.Contains(Pen.Dim($"{FileChangeKind.Unchanged,10}"), output);
    }

    [Fact]
    public void FormatDiff_OneLineFlippedAcrossOneFile_TrailerUsesSingularForBoth()
    {
        var before = new CoverageReport([new FileCoverage("a.cs", 1, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 5 }
        }]);
        var after = new CoverageReport([new FileCoverage("a.cs", 0, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 0 }
        }]);

        var output = TableFormatter.FormatDiff(CoverageDiff.Compare(before, after));

        Assert.Contains("Indirect changes: 1 line flipped across 1 file", output);
    }

    [Fact]
    public void FormatDiff_NoIndirectChanges_OmitsTrailerLine()
    {
        var diff = CoverageDiff.Compare(
            new CoverageReport([new FileCoverage("a.cs", 5, 10, 0, 0)]),
            new CoverageReport([new FileCoverage("a.cs", 5, 10, 0, 0)]));

        var output = TableFormatter.FormatDiff(diff);

        Assert.DoesNotContain("Indirect changes", output);
    }

    [Fact]
    public void FormatDiff_MultipleLinesAndFilesAffected_TrailerUsesPluralForBoth()
    {
        // Pluralization branches: both "lines"/"line" and "files"/"file" toggle on count.
        var before = new CoverageReport([
            new FileCoverage("a.cs", 1, 1, 0, 0) { LineHits = new Dictionary<int, int> { [10] = 1 } },
            new FileCoverage("b.cs", 1, 1, 0, 0) { LineHits = new Dictionary<int, int> { [20] = 1 } }
        ]);
        var after = new CoverageReport([
            new FileCoverage("a.cs", 0, 1, 0, 0) { LineHits = new Dictionary<int, int> { [10] = 0 } },
            new FileCoverage("b.cs", 0, 1, 0, 0) { LineHits = new Dictionary<int, int> { [20] = 0 } }
        ]);

        var output = TableFormatter.FormatDiff(CoverageDiff.Compare(before, after));

        Assert.Contains("Indirect changes: 2 lines flipped across 2 files", output);
    }

    [Fact]
    public void FormatDiff_EmptyDiff_RendersHeaderOnly()
    {
        var output = TableFormatter.FormatDiff(
            CoverageDiff.Compare(CoverageReport.Empty, CoverageReport.Empty));

        Assert.Contains("File", output);
        Assert.Contains("TOTAL", output);
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

    [Fact]
    public void Format_ShortPathRow_PadsToTheLongestPath_SoColumnsAlign()
    {
        // The column width is Max(header, longest path) — asserting the padded ROW text
        // itself is what pins Max: a Min would still produce a long-enough divider (the
        // numeric columns alone exceed any path here), but it cannot align the cells.
        const string longPath = "very/long/nested/path/to/source.cs";
        var report = new CoverageReport([
            new FileCoverage("short.cs", 1, 2, 0, 0),
            new FileCoverage(longPath, 2, 2, 0, 0)
        ]);

        var lines = TableFormatter.Format(report).Split(Environment.NewLine);
        var shortRow = lines.Single(l => l.StartsWith("short.cs", StringComparison.Ordinal));
        var longRow = lines.Single(l => l.StartsWith("very/", StringComparison.Ordinal));

        // The path cell is padded to the longest path, so the short row carries the padding…
        Assert.StartsWith("short.cs" + new string(' ', longPath.Length - "short.cs".Length) + "  ",
            shortRow, StringComparison.Ordinal);
        // …and the right-aligned Lines cells start at the same absolute column in both rows.
        Assert.Equal(longPath.Length + 2 + 7, shortRow.IndexOf("1/2", StringComparison.Ordinal));
        Assert.Equal(longPath.Length + 2 + 7, longRow.IndexOf("2/2", StringComparison.Ordinal));
    }

    [Fact]
    public void FormatDiff_ShortPathRow_PadsToTheLongestPath_SoColumnsAlign()
    {
        // Same Max(header, longest path) contract on the diff table.
        const string longPath = "very/long/nested/path/to/source.cs";
        var before = new CoverageReport([
            new FileCoverage("short.cs", 1, 2, 0, 0),
            new FileCoverage(longPath, 1, 2, 0, 0)
        ]);
        var after = new CoverageReport([
            new FileCoverage("short.cs", 2, 2, 0, 0),
            new FileCoverage(longPath, 1, 2, 0, 0)
        ]);

        var lines = TableFormatter.FormatDiff(CoverageDiff.Compare(before, after))
            .Split(Environment.NewLine);
        var shortRow = lines.Single(l => l.StartsWith("short.cs", StringComparison.Ordinal));
        var longRow = lines.Single(l => l.StartsWith("very/", StringComparison.Ordinal));

        Assert.StartsWith("short.cs" + new string(' ', longPath.Length - "short.cs".Length) + "  ",
            shortRow, StringComparison.Ordinal);
        // Before cells (8 wide, right-aligned "   50.0%"/"  100.0%") start at the same column.
        Assert.Equal(longPath.Length + 2, shortRow.IndexOf("   50.0%", StringComparison.Ordinal));
        Assert.Equal(longPath.Length + 2, longRow.IndexOf("   50.0%", StringComparison.Ordinal));
    }

    [Fact]
    public void Format_UnmeasuredFile_SortsFirst_BeforeZeroAndCoveredFiles()
    {
        // The documented worst-first contract: unmeasured files (null line rate) outrank
        // even 0%-covered files — there is no number to vouch for at all. A positive null
        // fallback in the ordering key would silently bury them last.
        var report = new CoverageReport([
            new FileCoverage("covered.cs", 1, 2, 0, 0),
            new FileCoverage("zero.cs", 0, 3, 0, 0),
            new FileCoverage("unmeasured.cs", 0, 0, 0, 0)
        ]);

        var lines = TableFormatter.Format(report).Split(Environment.NewLine);

        Assert.StartsWith("unmeasured.cs", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("zero.cs", lines[3], StringComparison.Ordinal);
        Assert.StartsWith("covered.cs", lines[4], StringComparison.Ordinal);
    }

    // ── Warnings trailer: one-line `Warnings: N`, silent when empty ──

    [Fact]
    public void Format_NoWarnings_OmitsTrailerLine()
    {
        // Default-path clean output stays untouched.
        var output = TableFormatter.Format(Reports.Mixed);

        Assert.DoesNotContain("Warnings:", output);
    }

    [Fact]
    public void Format_WithWarnings_RendersCountTrailerAfterTotal()
    {
        // Trailer prints the count only; detailed entries live in the markdown formatter.
        // Verifies position too (after TOTAL) so visual scanning stays predictable.
        var report = new CoverageReport([new FileCoverage("src/A.cs", 1, 1, 0, 0)])
        {
            Warnings =
            [
                new CoverageWarning(CoverageWarningKind.BranchTotalMismatch, "src/A.cs", 1, "x"),
                new CoverageWarning(CoverageWarningKind.MalformedConditionCoverage, "src/A.cs", 2, "y")
            ]
        };

        var output = TableFormatter.Format(report);
        var lines = output.Split('\n');

        Assert.Contains("Warnings: 2", output);
        var totalIdx = Array.FindIndex(lines, l => l.Contains("TOTAL"));
        var warnIdx = Array.FindIndex(lines, l => l.Contains("Warnings:"));
        Assert.True(totalIdx < warnIdx, "Warnings trailer must follow TOTAL row");
    }

    [Fact]
    public void Format_WithWarnings_ColorEnabled_DimsTrailer()
    {
        // Dim ANSI escape on the trailer keeps it un-screaming in normal output —
        // consistent with the indirect-changes trailer style on the diff path.
        var report = new CoverageReport([new FileCoverage("src/A.cs", 1, 1, 0, 0)])
        {
            Warnings = [new CoverageWarning(CoverageWarningKind.BranchTotalMismatch, "src/A.cs", 1, "x")]
        };

        var output = TableFormatter.Format(report, color: true);
        var trailerLine = output.Split('\n').Single(l => l.Contains("Warnings:")).TrimEnd();

        Assert.Equal(Pen.Dim("Warnings: 1"), trailerLine);
    }
}
