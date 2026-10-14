using DotCov.Formatters;

namespace DotCov.Tests;

/// <summary>
/// The offender list through the library's public formatters for records the parser never
/// builds: a hand-built file carries counts without per-line data, so it has rates to blame but
/// no uncovered lines or branch lines to name, and a file can carry branches without lines.
/// </summary>
public sealed class OffenderFormatterTests
{
    // X: branches only, 1/4, counts without per-line data. Y: one covered line, so lines measure.
    private static readonly CoverageReport CountsOnly = new([
        new FileCoverage("src/X.cs", 0, 0, 1, 4),
        new FileCoverage("src/Y.cs", 1, 1, 0, 0),
    ]);

    private static string Lines(params string[] lines) => string.Join(Environment.NewLine, lines) + Environment.NewLine;

    [Test]
    public async Task Table_CountOnlyFile_GetsItsRates_AndNoDetailLine()
    {
        var gate = CountsOnly.Evaluate(0, 50);

        await Assert.That(gate.Outcome).IsEqualTo(GateOutcome.Fail);
        await Assert.That(TableFormatter.FormatOffenders(CountsOnly, gate)).IsEqualTo(Lines(
            "files below branch threshold:",
            "  src/X.cs  25.0% branches"));
    }

    [Test]
    public async Task Markdown_CountOnlyFile_GetsItsRates_AndNoDetail()
    {
        var gate = CountsOnly.Evaluate(0, 50);

        await Assert.That(MarkdownFormatter.Format(CountsOnly, gate)).Contains(Lines(
            "### Files below branch threshold",
            "",
            "- `src/X.cs` — 25.0% branches"));
    }

    [Test]
    public async Task PassingGate_BlamesNoFile()
    {
        var gate = CountsOnly.Evaluate(0, 20);

        await Assert.That(gate.Outcome).IsEqualTo(GateOutcome.Pass);
        await Assert.That(TableFormatter.FormatOffenders(CountsOnly, gate)).IsEmpty();
        await Assert.That(MarkdownFormatter.Format(CountsOnly, gate)).DoesNotContain("Files below");
    }
}
