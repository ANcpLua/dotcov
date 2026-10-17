using DotCov.Formatters;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;
/// <summary>
/// <see cref = "AnsiPen.Rate"/> reserves the dim arm for a rate that does not exist. A measured
/// 0% is the worst verdict there is and must be painted red, never dimmed like an unmeasured
/// "-" cell.
/// </summary>
[TestClass]
public sealed class AnsiPenEdgeTests
{
    private static readonly AnsiPen Pen = new(enabled: true);
    [TestMethod]
    public void Rate_MeasuredZero_IsRedNotDim()
    {
        Assert.AreEqual(Pen.Red("X"), Pen.Rate("X", 0.0));
    }

    [TestMethod]
    public void Rate_Unmeasured_IsDim()
    {
        Assert.AreEqual(Pen.Dim("X"), Pen.Rate("X", null));
    }

    [TestMethod]
    public void Rate_NotANumber_IsDimLikeUnmeasured()
    {
        // NaN is no rate at all (0/0 from a hand-built record), so no colour verdict either.
        Assert.AreEqual(Pen.Dim("X"), Pen.Rate("X", double.NaN));
    }

    [TestMethod]
    public void Table_ZeroCoveredFile_IsRedWhileUnmeasuredFileIsDim()
    {
        var report = new CoverageReport([new FileCoverage("src/Untested.cs", 0, 3, 0, 4), new FileCoverage("src/Empty.cs", 0, 0, 0, 0)]);
        var output = TableFormatter.Format(report, color: true);
        var untested = Assert.ContainsSingle(l => l.Contains("Untested.cs", StringComparison.Ordinal), output.Split(Environment.NewLine));
        var empty = Assert.ContainsSingle(l => l.Contains("Empty.cs", StringComparison.Ordinal), output.Split(Environment.NewLine));
        Assert.Contains(Pen.Red("    0.0%"), untested);
        Assert.Contains(Pen.Red($"{"0/4", 10}"), untested);
        Assert.Contains(Pen.Dim("       -"), empty);
        Assert.IsFalse(AnsiStrip.ContainsAnsi(TableFormatter.Format(report, color: false)));
    }
}
