using DotCov.Formatters;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

/// <summary>
/// <see cref="AnsiPen.Rate"/> reserves the dim arm for a rate that does not exist. A measured
/// 0% is the worst verdict there is and must be painted red, never dimmed like an unmeasured
/// "-" cell.
/// </summary>
public sealed class AnsiPenEdgeTests
{
    private static readonly AnsiPen Pen = new(enabled: true);

    [Test]
    public async Task Rate_MeasuredZero_IsRedNotDim()
    {
        await Assert.That(Pen.Rate("X", 0.0)).IsEqualTo(Pen.Red("X"));
    }

    [Test]
    public async Task Rate_Unmeasured_IsDim()
    {
        await Assert.That(Pen.Rate("X", null)).IsEqualTo(Pen.Dim("X"));
    }

    [Test]
    public async Task Rate_NotANumber_IsDimLikeUnmeasured()
    {
        // NaN is no rate at all (0/0 from a hand-built record), so no colour verdict either.
        await Assert.That(Pen.Rate("X", double.NaN)).IsEqualTo(Pen.Dim("X"));
    }

    [Test]
    public async Task Table_ZeroCoveredFile_IsRedWhileUnmeasuredFileIsDim()
    {
        var report = new CoverageReport([
            new FileCoverage("src/Untested.cs", 0, 3, 0, 4),
            new FileCoverage("src/Empty.cs", 0, 0, 0, 0)
        ]);

        var output = TableFormatter.Format(report, color: true);

        var untested = output.Split(Environment.NewLine).Single(l => l.Contains("Untested.cs", StringComparison.Ordinal));
        var empty = output.Split(Environment.NewLine).Single(l => l.Contains("Empty.cs", StringComparison.Ordinal));
        await Assert.That(untested).Contains(Pen.Red("    0.0%"));
        await Assert.That(untested).Contains(Pen.Red($"{"0/4",10}"));
        await Assert.That(empty).Contains(Pen.Dim("       -"));
        await Assert.That(AnsiStrip.ContainsAnsi(TableFormatter.Format(report, color: false))).IsFalse();
    }
}
