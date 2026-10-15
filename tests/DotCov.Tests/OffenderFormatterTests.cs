using DotCov.Formatters;

namespace DotCov.Tests;
/// <summary>
/// The offender list through the library's public formatters for records the parser never
/// builds: a hand-built file carries counts without per-line data, so it has rates to blame but
/// no uncovered lines or branch lines to name, and a file can carry branches without lines.
/// </summary>
[TestClass]
public sealed class OffenderFormatterTests
{
    // X: branches only, 1/4, counts without per-line data. Y: one covered line, so lines measure.
    private static readonly CoverageReport CountsOnly = new([new FileCoverage("src/X.cs", 0, 0, 1, 4), new FileCoverage("src/Y.cs", 1, 1, 0, 0), ]);
    private static string Lines(params string[] lines) => string.Join(Environment.NewLine, lines) + Environment.NewLine;
    [TestMethod]
    public void Table_CountOnlyFile_GetsItsRates_AndNoDetailLine()
    {
        var gate = CountsOnly.Evaluate(0, 50);
        Assert.AreEqual(GateOutcome.Fail, gate.Outcome);
        Assert.AreEqual(Lines("files below branch threshold:", "  src/X.cs  25.0% branches"), TableFormatter.FormatOffenders(CountsOnly, gate));
    }

    [TestMethod]
    public void Markdown_CountOnlyFile_GetsItsRates_AndNoDetail()
    {
        var gate = CountsOnly.Evaluate(0, 50);
        Assert.Contains(Lines("### Files below branch threshold", "", "- `src/X.cs` — 25.0% branches"), MarkdownFormatter.Format(CountsOnly, gate));
    }

    [TestMethod]
    public void PassingGate_BlamesNoFile()
    {
        var gate = CountsOnly.Evaluate(0, 20);
        Assert.AreEqual(GateOutcome.Pass, gate.Outcome);
        Assert.IsEmpty(TableFormatter.FormatOffenders(CountsOnly, gate));
        Assert.DoesNotContain("Files below", MarkdownFormatter.Format(CountsOnly, gate));
    }
}
