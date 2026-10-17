using DotCov.Formatters;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;
[TestClass]
public sealed class TableFormatterTests
{
    private static readonly AnsiPen Pen = new(enabled: true);
    // Derives a style's opening escape from AnsiPen itself, so these tests assert
    // "this cell is styled by pen X" without re-pinning the byte-level codes —
    // those are pinned exactly once, in AnsiPenTests.
    private static string Open(Func<string, string> style) => style("\0").Split('\0')[0];
    [TestMethod]
    public void Format_DefaultPlain_ContainsNoAnsiEscapes()
    {
        var output = TableFormatter.Format(Reports.Mixed);
        Assert.IsFalse(AnsiStrip.ContainsAnsi(output));
    }

    [TestMethod]
    public void Format_ExplicitColorOff_ContainsNoAnsiEscapes()
    {
        var output = TableFormatter.Format(Reports.Mixed, color: false);
        Assert.IsFalse(AnsiStrip.ContainsAnsi(output));
    }

    [TestMethod]
    public void Format_ColorEnabled_EmitsAnsiEscapes()
    {
        var output = TableFormatter.Format(Reports.Mixed, color: true);
        Assert.IsTrue(AnsiStrip.ContainsAnsi(output));
    }

    [TestMethod]
    public void Format_StrippedColoredOutput_MatchesPlainOutput()
    {
        var plain = TableFormatter.Format(Reports.Mixed, color: false);
        var colored = TableFormatter.Format(Reports.Mixed, color: true);
        Assert.AreEqual(plain, AnsiStrip.From(colored));
    }

    [TestMethod]
    public void Format_RowsOrderedByLineRateAscending()
    {
        var output = TableFormatter.Format(Reports.Mixed);
        var lines = output.Split('\n');
        var unusedRow = Array.FindIndex(lines, l => l.Contains("Unused.cs"));
        var parserRow = Array.FindIndex(lines, l => l.Contains("Parser.cs"));
        var calcRow = Array.FindIndex(lines, l => l.Contains("Calculator.cs"));
        Assert.IsGreaterThanOrEqualTo(0, unusedRow);
        Assert.IsGreaterThan(unusedRow, parserRow);
        Assert.IsGreaterThan(parserRow, calcRow);
    }

    [TestMethod]
    public void Format_NoBranchData_RendersDashInsteadOfHundredPercent()
    {
        var output = TableFormatter.Format(Reports.LinesOnly);
        var row = Assert.ContainsSingle(l => l.Contains("App.cs"), output.Split('\n')).TrimEnd();
        // Anchored to the file's own row: line columns populated, both branch columns dashed.
        Assert.MatchesRegex(@"^src/App\.cs\s+410/769\s+53\.3%\s+-\s+-$", row);
    }

    [TestMethod]
    public void Format_TotalRow_ContainsCorrectAggregateNumbers()
    {
        var output = TableFormatter.Format(Reports.Mixed);
        Assert.Contains("TOTAL", output);
        Assert.Contains("7/12", output); // 4+3+0 hit / 4+5+3 total
    }

    [TestMethod]
    public void Format_FileWithBranches_PopulatesBranchCell()
    {
        var output = TableFormatter.Format(Reports.Mixed);
        var calcRow = Assert.ContainsSingle(l => l.Contains("Calculator.cs"), output.Split('\n'));
        Assert.Contains("2/2", calcRow);
    }

    [TestMethod]
    public void Format_TotalRow_PopulatesAggregateBranchCell()
    {
        // TOTAL branch cell is the branch aggregate (2+1+0)/(2+4+0), not the lines cell.
        var output = TableFormatter.Format(Reports.Mixed);
        var totalRow = Assert.ContainsSingle(l => l.Contains("TOTAL"), output.Split('\n'));
        Assert.Contains("3/6", totalRow);
    }

    [TestMethod]
    public void Format_NoBranchData_TotalRowRendersDashInBranchColumns()
    {
        var output = TableFormatter.Format(Reports.LinesOnly);
        var totalRow = Assert.ContainsSingle(l => l.Contains("TOTAL"), output.Split('\n')).TrimEnd();
        // Anchored to the TOTAL row itself: line columns populated, both branch columns dashed.
        Assert.MatchesRegex(@"^TOTAL\s+410/769\s+53\.3%\s+-\s+-$", totalRow);
    }

    [TestMethod]
    public void Format_ColorEnabled_PaintsBranchCellsByBranchRate()
    {
        // Branch cells are painted by pen.Rate over the branch rate — per-file
        // (Calculator.cs: 2/2 → green) and TOTAL (3/6 = 50% → yellow, bold).
        var output = TableFormatter.Format(Reports.Mixed, color: true);
        // Asserted as the adjacent branches+branch% cell pair so the line-% cell (also
        // green "  100.0%" for Calculator.cs) cannot satisfy the branch-cell assertion.
        Assert.Contains($"{Pen.Rate($"{"2/2", 10}", 1.0)}  {Pen.Rate("  100.0%", 1.0)}", output);
        Assert.Contains($"{Pen.Bold(Pen.Rate($"{"3/6", 10}", 0.5))}  {Pen.Bold(Pen.Rate("   50.0%", 0.5))}", output);
    }

    [TestMethod]
    public void Format_EmptyReport_RendersHeaderAndTotalOnly()
    {
        var output = TableFormatter.Format(Reports.Empty);
        Assert.Contains("File", output);
        Assert.Contains("TOTAL", output);
        Assert.Contains("0/0", output);
    }

    [TestMethod]
    public void Format_ColorEnabled_BoldsTotalRow()
    {
        var output = TableFormatter.Format(Reports.Mixed, color: true);
        var totalLine = Assert.ContainsSingle(l => l.Contains("TOTAL"), output.Split('\n'));
        Assert.Contains(Open(Pen.Bold), totalLine);
    }

    [TestMethod]
    public void Format_ColorEnabled_PaintsCyanBoldHeader()
    {
        var output = TableFormatter.Format(Reports.Mixed, color: true);
        var headerLine = output.Split('\n')[0].TrimEnd();
        // The header is exactly its plain text run through Bold(Cyan(…)) — rebuilt from
        // an AnsiPen so a palette change in the pen cannot break a formatter test.
        Assert.AreEqual(Pen.Bold(Pen.Cyan(AnsiStrip.From(headerLine))), headerLine);
    }

    [TestMethod]
    public void FormatDiff_DefaultPlain_NoAnsi()
    {
        var diff = CoverageDiff.Compare(Reports.Single("a.cs", hit: 5, total: 10), Reports.Single("a.cs", hit: 8, total: 10));
        Assert.IsFalse(AnsiStrip.ContainsAnsi(TableFormatter.FormatDiff(diff)));
    }

    [TestMethod]
    public void FormatDiff_ColorEnabled_PaintsDeltaByDirection()
    {
        var diff = CoverageDiff.Compare(new CoverageReport([new FileCoverage("regress.cs", 9, 10, 0, 0)]), new CoverageReport([new FileCoverage("regress.cs", 6, 10, 0, 0)]));
        var output = TableFormatter.FormatDiff(diff, color: true);
        // The regression's delta cell is painted by the pen's delta rule (red arm).
        // Cell is the full 8-char unit — sign attached to the digits, then right-aligned.
        Assert.Contains(Pen.Delta("  -30.0%", diff.Delta), output);
    }

    [TestMethod]
    public void FormatDiff_ColorEnabled_PaintsAddedGreen()
    {
        var diff = CoverageDiff.Compare(CoverageReport.Empty, new CoverageReport([new FileCoverage("new.cs", 5, 10, 0, 0)]));
        var output = TableFormatter.FormatDiff(diff, color: true);
        Assert.Contains(Pen.Green($"{FileChangeKind.Added, 10}"), output);
    }

    [TestMethod]
    public void FormatDiff_RemovedFile_BeforeShownAfterDashed()
    {
        var diff = CoverageDiff.Compare(new CoverageReport([new FileCoverage("gone.cs", 4, 5, 0, 0)]), CoverageReport.Empty);
        var output = TableFormatter.FormatDiff(diff, color: true);
        var row = Assert.ContainsSingle(l => l.Contains("gone.cs"), AnsiStrip.From(output).Split('\n')).TrimEnd();
        // Before populated, after dashed, delta -before — pinned on the file's own row so
        // a dash elsewhere (TOTAL row, other columns) cannot satisfy the assertion.
        Assert.MatchesRegex(@"^gone\.cs\s+80\.0%\s+-\s+-80\.0%\s+Removed$", row);
    }

    [TestMethod]
    public void FormatDiff_AddedFile_BeforeDashedAfterShown()
    {
        var diff = CoverageDiff.Compare(CoverageReport.Empty, new CoverageReport([new FileCoverage("fresh.cs", 7, 10, 0, 0)]));
        var output = TableFormatter.FormatDiff(diff);
        var row = Assert.ContainsSingle(l => l.Contains("fresh.cs"), output.Split('\n')).TrimEnd();
        // The '+' sign is attached to its digits — never a detached "+  70.0%".
        Assert.MatchesRegex(@"^fresh\.cs\s+-\s+70\.0%\s+\+70\.0%\s+Added$", row);
    }

    // ── Delta cell policy: sign attached to digits, one 8-char unit, rows and TOTAL alike ──
    [TestMethod]
    public void FormatDiff_TotalRow_RendersAggregateDeltaWithAttachedSign()
    {
        // Multi-file diff whose TOTAL delta (+25.0%) differs from every per-file delta
        // (+30.0%, +20.0%), so this pins the TOTAL arithmetic itself — a whole-output
        // Contains would otherwise be satisfied by a per-file cell.
        var diff = CoverageDiff.Compare(new CoverageReport([new FileCoverage("a.cs", 5, 10, 0, 0), new FileCoverage("b.cs", 5, 10, 0, 0)]), new CoverageReport([new FileCoverage("a.cs", 8, 10, 0, 0), new FileCoverage("b.cs", 7, 10, 0, 0)]));
        var output = TableFormatter.FormatDiff(diff);
        var totalRow = Assert.ContainsSingle(l => l.Contains("TOTAL"), output.Split('\n')).TrimEnd();
        Assert.MatchesRegex(@"^TOTAL\s+50\.0%\s+75\.0%\s+\+25\.0%$", totalRow);
        Assert.Contains("  +25.0%", totalRow); // full 8-char cell: sign never detaches
    }

    [TestMethod]
    public void FormatDiff_ZeroDelta_RowAndTotalUseSamePlusZeroCell()
    {
        // One sign policy everywhere: zero renders '+0.0%' in per-file rows and TOTAL alike. A
        // listed file moves by zero only when it was added at 0%.
        var added = CoverageDiff.Compare(Reports.Single("a.cs", hit: 5, total: 10), new CoverageReport([new FileCoverage("a.cs", 5, 10, 0, 0), new FileCoverage("zero.cs", 0, 10, 0, 0)]));
        var still = CoverageDiff.Compare(Reports.Single("same.cs", hit: 5, total: 10), Reports.Single("same.cs", hit: 5, total: 10));
        var fileRow = Assert.ContainsSingle(l => l.Contains("zero.cs"), TableFormatter.FormatDiff(added).Split('\n')).TrimEnd();
        var totalRow = Assert.ContainsSingle(l => l.Contains("TOTAL"), TableFormatter.FormatDiff(still).Split('\n')).TrimEnd();
        Assert.Contains("   +0.0%", fileRow);
        Assert.Contains("   +0.0%", totalRow);
    }

    [TestMethod]
    public void FormatDiff_NegativeDelta_CellIsEightCharsWithOwnMinusSign()
    {
        var diff = CoverageDiff.Compare(Reports.Single("down.cs", hit: 9, total: 10), Reports.Single("down.cs", hit: 6, total: 10));
        var output = TableFormatter.FormatDiff(diff);
        var fileRow = Assert.ContainsSingle(l => l.Contains("down.cs"), output.Split('\n')).TrimEnd();
        var totalRow = Assert.ContainsSingle(l => l.Contains("TOTAL"), output.Split('\n')).TrimEnd();
        // Negative cells align with every other delta state: 8 chars, no '+' anywhere.
        Assert.Contains("  -30.0%", fileRow);
        Assert.Contains("  -30.0%", totalRow);
        Assert.DoesNotContain("+", fileRow);
    }

    [TestMethod]
    public void FormatDiff_NullDelta_RendersDashCell()
    {
        // An added 0/0 file has no rate on either side → null delta → the 8-char dash cell.
        var diff = CoverageDiff.Compare(CoverageReport.Empty, new CoverageReport([new FileCoverage("empty.cs", 0, 0, 0, 0)]));
        var output = TableFormatter.FormatDiff(diff);
        var fileRow = Assert.ContainsSingle(l => l.Contains("empty.cs"), output.Split('\n')).TrimEnd();
        Assert.MatchesRegex(@"^empty\.cs\s+-\s+-\s+-\s+Added$", fileRow);
        Assert.Contains("       -", fileRow); // dash keeps the 8-char cell width
    }

    [TestMethod]
    public void FormatDiff_UnchangedFiles_AreCountedOnADimLine_NotListed()
    {
        // A diff reads as what moved: files that stood still are counted, never rowed, and a
        // long unchanged path does not widen the table.
        var unchangedPath = "src/very/long/path/that/did/not/move/Unchanged.cs";
        var diff = CoverageDiff.Compare(new CoverageReport([new FileCoverage("a.cs", 5, 10, 0, 0), new FileCoverage(unchangedPath, 5, 10, 0, 0)]), new CoverageReport([new FileCoverage("a.cs", 8, 10, 0, 0), new FileCoverage(unchangedPath, 5, 10, 0, 0)]));
        var output = TableFormatter.FormatDiff(diff, color: true);
        var plain = AnsiStrip.From(output);
        Assert.DoesNotContain(unchangedPath, plain);
        Assert.DoesNotContain(nameof(FileChangeKind.Unchanged) + Environment.NewLine, plain);
        Assert.StartsWith("File     Before", plain.Split(Environment.NewLine)[0]);
        Assert.Contains(Pen.Dim("Unchanged: 1 file") + Environment.NewLine, output);
    }

    [TestMethod]
    public void FormatDiff_CountsSeveralUnchangedFiles_InThePlural()
    {
        var diff = CoverageDiff.Compare(new CoverageReport([new FileCoverage("a.cs", 5, 10, 0, 0), new FileCoverage("b.cs", 5, 10, 0, 0)]), new CoverageReport([new FileCoverage("a.cs", 5, 10, 0, 0), new FileCoverage("b.cs", 5, 10, 0, 0)]));
        var output = TableFormatter.FormatDiff(diff);
        // Nothing moved: the header, the TOTAL row aligned under it, and the count.
        const string header = "File     Before     After     Delta      Change";
        var rule = new string ('-', header.Length);
        Assert.AreEqual($"{header}{Environment.NewLine}{rule}{Environment.NewLine}{rule}{Environment.NewLine}" + $"TOTAL     50.0%     50.0%     +0.0%{Environment.NewLine}" + $"Unchanged: 2 files{Environment.NewLine}", output);
        Assert.DoesNotContain("a.cs", output);
    }

    [TestMethod]
    public void FormatDiff_OneLineFlippedAcrossOneFile_TrailerUsesSingularForBoth()
    {
        var before = new CoverageReport([new FileCoverage("a.cs", 1, 1, 0, 0) { LineHits = new Dictionary<int, int> { [10] = 5 } }]);
        var after = new CoverageReport([new FileCoverage("a.cs", 0, 1, 0, 0) { LineHits = new Dictionary<int, int> { [10] = 0 } }]);
        var output = TableFormatter.FormatDiff(CoverageDiff.Compare(before, after));
        Assert.Contains("Indirect changes: 1 line flipped across 1 file", output);
    }

    [TestMethod]
    public void FormatDiff_NoIndirectChanges_OmitsTrailerLine()
    {
        var diff = CoverageDiff.Compare(new CoverageReport([new FileCoverage("a.cs", 5, 10, 0, 0)]), new CoverageReport([new FileCoverage("a.cs", 5, 10, 0, 0)]));
        var output = TableFormatter.FormatDiff(diff);
        Assert.DoesNotContain("Indirect changes", output);
    }

    [TestMethod]
    public void FormatDiff_MultipleLinesAndFilesAffected_TrailerUsesPluralForBoth()
    {
        var before = new CoverageReport([new FileCoverage("a.cs", 1, 1, 0, 0) { LineHits = new Dictionary<int, int> { [10] = 1 } }, new FileCoverage("b.cs", 1, 1, 0, 0) { LineHits = new Dictionary<int, int> { [20] = 1 } }]);
        var after = new CoverageReport([new FileCoverage("a.cs", 0, 1, 0, 0) { LineHits = new Dictionary<int, int> { [10] = 0 } }, new FileCoverage("b.cs", 0, 1, 0, 0) { LineHits = new Dictionary<int, int> { [20] = 0 } }]);
        var output = TableFormatter.FormatDiff(CoverageDiff.Compare(before, after));
        Assert.Contains("Indirect changes: 2 lines flipped across 2 files", output);
    }

    [TestMethod]
    public void FormatDiff_EmptyDiff_RendersHeaderOnly()
    {
        var output = TableFormatter.FormatDiff(CoverageDiff.Compare(CoverageReport.Empty, CoverageReport.Empty));
        Assert.Contains("File", output);
        Assert.Contains("TOTAL", output);
    }

    [TestMethod]
    public void Format_PathColumnWidth_AdjustsToLongestPath()
    {
        var report = new CoverageReport([new FileCoverage("short.cs", 1, 1, 0, 0), new FileCoverage("very/long/nested/path/to/some/source.cs", 1, 1, 0, 0)]);
        var output = TableFormatter.Format(report);
        var lines = output.Split('\n');
        Assert.Contains("very/long/nested/path/to/some/source.cs", output);
        var dividerLength = lines.First(l => l.StartsWith("-")).Length;
        Assert.IsGreaterThan("very/long/nested/path/to/some/source.cs".Length, dividerLength);
    }

    [TestMethod]
    public void Format_ShortPathRow_PadsToTheLongestPath_SoColumnsAlign()
    {
        // The column width is Max(header, longest path) — asserting the padded ROW text
        // itself is what pins Max: a Min would still produce a long-enough divider (the
        // numeric columns alone exceed any path here), but it cannot align the cells.
        const string longPath = "very/long/nested/path/to/source.cs";
        var report = new CoverageReport([new FileCoverage("short.cs", 1, 2, 0, 0), new FileCoverage(longPath, 2, 2, 0, 0)]);
        var lines = TableFormatter.Format(report).Split(Environment.NewLine);
        var shortRow = Assert.ContainsSingle(l => l.StartsWith("short.cs", StringComparison.Ordinal), lines);
        var longRow = Assert.ContainsSingle(l => l.StartsWith("very/", StringComparison.Ordinal), lines);
        // The path cell is padded to the longest path, so the short row carries the padding…
        Assert.StartsWith("short.cs" + new string (' ', longPath.Length - "short.cs".Length) + "  ", shortRow);
        // …and the right-aligned Lines cells start at the same absolute column in both rows.
        Assert.AreEqual(longPath.Length + 2 + 7, shortRow.IndexOf("1/2", StringComparison.Ordinal));
        Assert.AreEqual(longPath.Length + 2 + 7, longRow.IndexOf("2/2", StringComparison.Ordinal));
    }

    [TestMethod]
    public void FormatDiff_ShortPathRow_PadsToTheLongestPath_SoColumnsAlign()
    {
        // Same Max(header, longest path) contract on the diff table.
        const string longPath = "very/long/nested/path/to/source.cs";
        var before = new CoverageReport([new FileCoverage("short.cs", 1, 2, 0, 0), new FileCoverage(longPath, 1, 2, 0, 0)]);
        var after = new CoverageReport([new FileCoverage("short.cs", 2, 2, 0, 0), new FileCoverage(longPath, 2, 2, 0, 0)]);
        var lines = TableFormatter.FormatDiff(CoverageDiff.Compare(before, after)).Split(Environment.NewLine);
        var shortRow = Assert.ContainsSingle(l => l.StartsWith("short.cs", StringComparison.Ordinal), lines);
        var longRow = Assert.ContainsSingle(l => l.StartsWith("very/", StringComparison.Ordinal), lines);
        Assert.StartsWith("short.cs" + new string (' ', longPath.Length - "short.cs".Length) + "  ", shortRow);
        // Before cells (8 wide, right-aligned "   50.0%"/"  100.0%") start at the same column.
        Assert.AreEqual(longPath.Length + 2, shortRow.IndexOf("   50.0%", StringComparison.Ordinal));
        Assert.AreEqual(longPath.Length + 2, longRow.IndexOf("   50.0%", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Format_UnmeasuredFile_SortsFirst_BeforeZeroAndCoveredFiles()
    {
        // The documented worst-first contract: unmeasured files (null line rate) outrank
        // even 0%-covered files — there is no number to vouch for at all. A positive null
        // fallback in the ordering key would silently bury them last.
        var report = new CoverageReport([new FileCoverage("covered.cs", 1, 2, 0, 0), new FileCoverage("zero.cs", 0, 3, 0, 0), new FileCoverage("unmeasured.cs", 0, 0, 0, 0)]);
        var lines = TableFormatter.Format(report).Split(Environment.NewLine);
        Assert.StartsWith("unmeasured.cs", lines[2]);
        Assert.StartsWith("zero.cs", lines[3]);
        Assert.StartsWith("covered.cs", lines[4]);
    }

    // ── Warnings trailer: one-line `Warnings: N`, silent when empty ──
    [TestMethod]
    public void Format_NoWarnings_OmitsTrailerLine()
    {
        var output = TableFormatter.Format(Reports.Mixed);
        Assert.DoesNotContain("Warnings:", output);
    }

    [TestMethod]
    public void Format_WithWarnings_RendersCountTrailerAfterTotal()
    {
        // Trailer prints the count only; detailed entries live in the markdown formatter.
        // Verifies position too (after TOTAL) so visual scanning stays predictable.
        var report = new CoverageReport([new FileCoverage("src/A.cs", 1, 1, 0, 0)])
        {
            Warnings = [new CoverageWarning(CoverageWarningKind.BranchTotalMismatch, "src/A.cs", 1, "x"), new CoverageWarning(CoverageWarningKind.MalformedConditionCoverage, "src/A.cs", 2, "y")]
        };
        var output = TableFormatter.Format(report);
        var lines = output.Split('\n');
        Assert.Contains("Warnings: 2", output);
        var totalIdx = Array.FindIndex(lines, l => l.Contains("TOTAL"));
        var warnIdx = Array.FindIndex(lines, l => l.Contains("Warnings:"));
        Assert.IsGreaterThanOrEqualTo(0, totalIdx);
        Assert.IsGreaterThan(totalIdx, warnIdx, message: "Warnings trailer must follow TOTAL row");
    }

    [TestMethod]
    public void Format_WithWarnings_ColorEnabled_DimsTrailer()
    {
        // Dim ANSI escape on the trailer keeps it un-screaming in normal output —
        // consistent with the indirect-changes trailer style on the diff path.
        var report = new CoverageReport([new FileCoverage("src/A.cs", 1, 1, 0, 0)])
        {
            Warnings = [new CoverageWarning(CoverageWarningKind.BranchTotalMismatch, "src/A.cs", 1, "x")]
        };
        var output = TableFormatter.Format(report, color: true);
        var trailerLine = Assert.ContainsSingle(l => l.Contains("Warnings:"), output.Split('\n')).TrimEnd();
        Assert.AreEqual(Pen.Dim("Warnings: 1"), trailerLine);
    }
}
