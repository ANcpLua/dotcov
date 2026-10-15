using DotCov.Formatters;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;
/// <summary>
/// Edge cases of <see cref = "MarkdownFormatter"/>: the threshold line must show the threshold
/// the badge was judged against, and file names that carry markdown syntax (<c>|</c>,
/// backticks, line breaks) must not change the table's shape or break out of their code span.
/// </summary>
[TestClass]
public sealed class MarkdownFormatterEdgeTests
{
    // ── Threshold line of the threshold overload ──────────────────────────────
    [TestMethod]
    public void Format_FractionalThreshold_ShowsTheThresholdTheBadgeWasJudgedAgainst()
    {
        // 798/1000 = 79.8% clears 79.6, so the badge is a pass. Printing the threshold as
        // "80%" would put a ✅ next to "79.8%" and "Threshold: 80%" - a pass that reads as a
        // failure.
        var report = Reports.Single("src/A.cs", hit: 798, total: 1000);
        var md = MarkdownFormatter.Format(report, threshold: 79.6);
        Assert.Contains("## Coverage Report ✅", md);
        Assert.Contains("**Threshold:** 79.6%", md);
        Assert.DoesNotContain("**Threshold:** 80%", md);
    }

    [TestMethod]
    public void Format_ThresholdBelowOnePercent_IsNotShownAsADisabledZeroThreshold()
    {
        // 0.4 is an armed threshold (Evaluate fails 2/1000 against it). "Threshold: 0%" next to
        // a ❌ claims a failure against a threshold nothing can fail.
        var report = Reports.Single("src/A.cs", hit: 2, total: 1000);
        var md = MarkdownFormatter.Format(report, threshold: 0.4);
        Assert.Contains("## Coverage Report ❌", md);
        Assert.Contains("**Threshold:** 0.4%", md);
    }

    [TestMethod]
    public void Format_IntegerThreshold_KeepsItsShape()
    {
        var md = MarkdownFormatter.Format(Reports.FullyCovered, threshold: 80);
        Assert.Contains($"**Threshold:** 80%{Environment.NewLine}", md);
    }

    [TestMethod]
    public void Format_FractionalThreshold_UnderCommaDecimalCulture_StaysInvariant()
    {
        var md = CultureScope.RenderWithCommaDecimal(() => MarkdownFormatter.Format(Reports.FullyCovered, threshold: 79.6));
        Assert.Contains("**Threshold:** 79.6%", md);
        Assert.DoesNotContain("79,6", md);
    }

    // ── Gate overload: floor flags come from the gate itself ──────────────────
    [TestMethod]
    public void GateOverload_PassingGate_FloorsNothing()
    {
        // 2499/2500 = 99.96% passes 90: a passing dimension must render rounded (100.0%),
        // never floored to 99.9%.
        var report = Reports.Single("src/A.cs", hit: 2499, total: 2500, bHit: 2499, bTotal: 2500);
        var gate = report.Evaluate(90, 90);
        var md = MarkdownFormatter.Format(report, gate);
        Assert.AreEqual(GateOutcome.Pass, gate.Outcome);
        Assert.Contains("**Line coverage:** 100.0% (2499/2500)", md);
        Assert.Contains("**Branch coverage:** 100.0% (2499/2500)", md);
        Assert.DoesNotContain("99.9%", md);
    }

    [TestMethod]
    public void GateOverload_BothDimensionsFail_FloorsBoth()
    {
        var report = Reports.Single("src/A.cs", hit: 1999, total: 2500, bHit: 1999, bTotal: 2500);
        var gate = report.Evaluate(80, 80);
        var md = MarkdownFormatter.Format(report, gate);
        Assert.Contains("**Line coverage:** 79.9% (1999/2500)", md);
        Assert.Contains("**Branch coverage:** 79.9% (1999/2500)", md);
        Assert.Contains("| `src/A.cs` | 1999/2500 | 79.9% | 1999/2500 | 79.9% |", md);
        Assert.DoesNotContain("80.0%", md);
    }

    // ── File names carrying markdown syntax ───────────────────────────────────
    [TestMethod]
    public void Format_PipeInFileName_KeepsTheRowAtFiveCells()
    {
        // '|' is legal in Linux file names. GFM splits table cells on every unescaped pipe,
        // code spans included, so an unescaped one would shift the row: the Lines column would
        // show "b.cs`" and the branch rate would fall off the end.
        var report = Reports.Single("src/a|b.cs", hit: 1, total: 2);
        var md = MarkdownFormatter.Format(report);
        var row = Line(md, "src/a");
        Assert.AreEqual(@"| `src/a\|b.cs` | 1/2 | 50.0% | - | - |", row);
        Assert.AreEqual(Cells(Line(md, "| File |")).Count, Cells(row).Count);
    }

    [TestMethod]
    public void Format_BacktickInFileName_StaysOneCodeSpan()
    {
        // A backtick inside a single-backtick span closes it early; the fence must be longer
        // than the longest backtick run, padded so CommonMark strips exactly the padding.
        var report = Reports.Single("src/`tick`.cs", hit: 1, total: 1);
        var md = MarkdownFormatter.Format(report);
        Assert.Contains("| `` src/`tick`.cs `` | 1/1 | 100.0% | - | - |", md);
    }

    [TestMethod]
    public void Format_LineBreakInFileName_KeepsTheRowOnOneLine()
    {
        var report = Reports.Single("src/a\nb.cs", hit: 1, total: 1);
        var md = MarkdownFormatter.Format(report);
        Assert.Contains($"| `src/a b.cs` | 1/1 | 100.0% | - | - |{Environment.NewLine}", md);
    }

    [TestMethod]
    public void Format_WarningForFileWithBacktick_StaysOneCodeSpan()
    {
        var report = new CoverageReport([new FileCoverage("src/a`b.cs", 1, 1, 0, 0)])
        {
            Warnings = [new CoverageWarning(CoverageWarningKind.MalformedHits, "src/a`b.cs", 3, "hits='x' could not be parsed - treating as 0")]
        };
        var md = MarkdownFormatter.Format(report);
        Assert.Contains("- `` src/a`b.cs:3 `` — MalformedHits: hits='x' could not be parsed - treating as 0", md);
    }

    [TestMethod]
    public void FormatDiff_PipeInFileName_KeepsTheRowAtFiveCells()
    {
        var before = new CoverageReport([new FileCoverage("src/a|b.cs", 1, 2, 0, 0) { LineHits = new Dictionary<int, int> { [1] = 1, [2] = 0 } }]);
        var after = new CoverageReport([new FileCoverage("src/a|b.cs", 2, 2, 0, 0) { LineHits = new Dictionary<int, int> { [1] = 1, [2] = 1 } }]);
        var md = MarkdownFormatter.FormatDiff(CoverageDiff.Compare(before, after));
        var row = Line(md, "| `src/a");
        Assert.AreEqual(@"| `src/a\|b.cs` | 50.0% | 100.0% | +50.0% | Modified |", row);
        Assert.AreEqual(Cells(Line(md, "| File |")).Count, Cells(row).Count);
        // Outside a table a pipe needs no escape, and a backslash would render literally.
        Assert.Contains("- `src/a|b.cs`: 1 newly hit", md);
    }

    // ── Diff header icon shares the movement threshold ─────────────────────────
    [TestMethod]
    [DataRow(20000, 19999, "➡️")] // -0.005pp: below MovementEpsilon, every file Unchanged
    [DataRow(19999, 20000, "➡️")] // +0.005pp: likewise noise
    [DataRow(10000, 9998, "📉")] // -0.02pp: clearly a regression
    [DataRow(9998, 10000, "📈")] // +0.02pp: clearly an improvement
    public void FormatDiff_HeaderIcon_UsesTheDiffMovementThreshold(int beforeHit, int afterHit, string icon)
    {
        // The header icon, the change kind and the delta colour must agree on whether coverage
        // moved: a sub-epsilon wobble is Unchanged and painted dim, so it must not read 📉/📈.
        var total = Math.Max(beforeHit, afterHit);
        var diff = CoverageDiff.Compare(Reports.Single("a.cs", beforeHit, total), Reports.Single("a.cs", afterHit, total));
        var md = MarkdownFormatter.FormatDiff(diff);
        Assert.AreEqual($"## Coverage Diff {icon}", md.Split(Environment.NewLine)[0]);
        var expectedKind = icon is "➡️" ? FileChangeKind.Unchanged : FileChangeKind.Modified;
        Assert.AreEqual(expectedKind, diff.Files.Single().Change);
    }

    private static string Line(string md, string marker) => md.Split(Environment.NewLine).First(l => l.Contains(marker, StringComparison.Ordinal));
    /// <summary>GFM cell split: on every pipe not escaped by a backslash, outer pipes trimmed.</summary>
    private static List<string> Cells(string row)
    {
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        for (var i = 1; i < row.Length - 1; i++)
        {
            if (row[i] == '|' && row[i - 1] != '\\')
            {
                cells.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(row[i]);
            }
        }

        cells.Add(current.ToString());
        return cells;
    }
}
