using DotCov.Formatters;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

/// <summary>
/// The gate-aware <see cref="TableFormatter.Format(CoverageReport, GateResult, bool)"/>: it
/// floors exactly what <see cref="MarkdownFormatter.Format(CoverageReport, GateResult, int)"/>
/// floors — the TOTAL of a dimension the gate failed, and the per-file cells below the missed
/// minimum — so a table logged next to <c>FAIL: line 99.9% (min 100%)</c> can never show a
/// TOTAL of <c>100.0%</c>. Everything else, and every passing gate, renders byte-identical to
/// <see cref="TableFormatter.Format(CoverageReport, bool, int?)"/>.
/// </summary>
public sealed class TableFormatterEdgeTests
{
    private static readonly AnsiPen Pen = new(enabled: true);

    [Test]
    public async Task GateOverload_FailingLineAtHundred_FloorsTotalAndFileRow()
    {
        // 19999/20000 = 99.995%: the plain table rounds both cells to 100.0% while the gate
        // printed beside it reads FAIL: line 99.9% (min 100%).
        var report = Reports.Single("src/A.cs", hit: 19999, total: 20000);
        var gate = report.Evaluate(100);

        var table = TableFormatter.Format(report, gate);

        await Assert.That(Row(table, "TOTAL")).IsEqualTo("TOTAL     19999/20000     99.9%           -         -");
        await Assert.That(Row(table, "src/A.cs")).IsEqualTo("src/A.cs  19999/20000     99.9%           -         -");
        await Assert.That(table).DoesNotContain("100.0%");
        await Assert.That(TableFormatter.Format(report)).Contains("100.0%");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GateOverload_PassingGate_IsByteIdenticalToPlainFormat(bool color)
    {
        // 2499/2500 = 99.96% passes 90 and must stay 100.0%; nothing floors on a pass.
        var report = new CoverageReport([
            new FileCoverage("src/High.cs", 2499, 2500, 2499, 2500),
            new FileCoverage("src/Empty.cs", 0, 0, 0, 0)
        ]);
        var gate = report.Evaluate(90, 90);

        await Assert.That(gate.Outcome).IsEqualTo(GateOutcome.Pass);
        await Assert.That(TableFormatter.Format(report, gate, color)).IsEqualTo(TableFormatter.Format(report, color));
    }

    [Test]
    public async Task GateOverload_DisabledGate_IsByteIdenticalToPlainFormat()
    {
        var report = Reports.Single("src/A.cs", hit: 1999, total: 2500);

        await Assert.That(TableFormatter.Format(report, report.Evaluate(0))).IsEqualTo(TableFormatter.Format(report));
    }

    [Test]
    public async Task GateOverload_WithoutColorArgument_MatchesColorOff()
    {
        var report = Reports.Single("src/A.cs", hit: 1999, total: 2500);
        var gate = report.Evaluate(80);

        await Assert.That(TableFormatter.Format(report, gate)).IsEqualTo(TableFormatter.Format(report, gate, color: false));
    }

    [Test]
    public async Task GateOverload_FailingLine_FloorsOnlyFilesBelowTheMinimum()
    {
        // TOTAL 2599/5000 = 51.98% fails 80 and floors to 51.9%; High (99.96%) clears 80 and
        // keeps 100.0%; Low (4%) is below and floors; the unmeasured file stays "-".
        var report = new CoverageReport([
            new FileCoverage("src/High.cs", 2499, 2500, 0, 0),
            new FileCoverage("src/Low.cs", 100, 2500, 0, 0),
            new FileCoverage("src/Empty.cs", 0, 0, 0, 0)
        ]);
        var gate = report.Evaluate(80);

        var table = TableFormatter.Format(report, gate);

        await Assert.That(Row(table, "TOTAL")).Contains("    51.9%");
        await Assert.That(Row(table, "src/High.cs")).Contains("   100.0%");
        await Assert.That(Row(table, "src/Low.cs")).Contains("     4.0%");
        await Assert.That(Row(table, "src/Empty.cs")).IsEqualTo("src/Empty.cs         0/0         -           -         -");
        await Assert.That(table).DoesNotContain("52.0%");
    }

    [Test]
    public async Task GateOverload_FailingBranchOnly_FloorsBranchCellsAndLeavesLineCells()
    {
        // Line 2499/2500 passes 90 (stays 100.0%); branches 1999/2500 = 79.96% fail 80 and
        // floor to 79.9% in the file row and TOTAL. The branch-less file keeps its dashes.
        var report = new CoverageReport([
            new FileCoverage("src/B.cs", 2499, 2500, 1999, 2500),
            new FileCoverage("src/NoBranches.cs", 10, 10, 0, 0)
        ]);
        var gate = report.Evaluate(90, 80);

        var table = TableFormatter.Format(report, gate);

        await Assert.That(Row(table, "src/B.cs")).IsEqualTo("src/B.cs            2499/2500    100.0%   1999/2500     79.9%");
        await Assert.That(Row(table, "TOTAL")).IsEqualTo("TOTAL               2509/2510    100.0%   1999/2500     79.9%");
        await Assert.That(Row(table, "src/NoBranches.cs")).IsEqualTo("src/NoBranches.cs       10/10    100.0%           -         -");
        await Assert.That(table).DoesNotContain("80.0%");
    }

    [Test]
    public async Task GateOverload_FlooredRateOnATenth_IsNotLoweredAnotherTenth()
    {
        // 4/5 = 80% fails 90; the floor's epsilon keeps it at 80.0%, never 79.9%.
        var report = Reports.Single("src/A.cs", hit: 4, total: 5);

        var table = TableFormatter.Format(report, report.Evaluate(90));

        await Assert.That(Row(table, "TOTAL")).Contains("    80.0%");
        await Assert.That(table).DoesNotContain("79.9%");
    }

    [Test]
    public async Task GateOverload_Color_PaintsTheFlooredCellByItsRate()
    {
        var report = Reports.Single("src/A.cs", hit: 19999, total: 20000);

        var table = TableFormatter.Format(report, report.Evaluate(100), color: true);

        await Assert.That(table).Contains(Pen.Rate("   99.9%", 0.99995));
        await Assert.That(table).Contains(Pen.Bold(Pen.Rate("   99.9%", 0.99995)));
        await Assert.That(AnsiStrip.From(table)).DoesNotContain("100.0%");
    }

    private static string Row(string table, string start) =>
        table.Split(Environment.NewLine).Single(l => l.StartsWith(start, StringComparison.Ordinal));
}
