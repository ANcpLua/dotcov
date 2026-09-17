using System.Text.Json;
using DotCov.Formatters;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

/// <summary>
/// Formatter output lands in CI logs, PR summaries, and machine-parsed JSON, so its shape
/// must not follow the host locale (a de-AT host writes 58,3 for 58.3 under current-culture
/// formatting). CurrentCulture is thread state: each test wraps its synchronous render in a
/// <see cref="CultureScope"/> and restores the culture before returning.
/// </summary>
public sealed class FormatterCultureTests
{
    [Test]
    public async Task TableFormat_UnderCommaDecimalCulture_UsesDotDecimals()
    {
        var output = CultureScope.RenderWithCommaDecimal(() => TableFormatter.Format(Reports.Mixed));

        await Assert.That(output).Contains("58.3%"); // TOTAL: 7/12 lines
        await Assert.That(output).DoesNotContain("58,3");
    }

    [Test]
    public async Task TableFormatDiff_UnderCommaDecimalCulture_UsesDotDecimals()
    {
        var diff = CoverageDiff.Compare(
            Reports.Single("a.cs", hit: 5, total: 10),
            Reports.Single("a.cs", hit: 8, total: 10));

        var output = CultureScope.RenderWithCommaDecimal(() => TableFormatter.FormatDiff(diff));

        await Assert.That(output).Contains("50.0%");
        await Assert.That(output).Contains("80.0%");
        await Assert.That(output).Contains("30.0%"); // delta column, invariant-formatted
        await Assert.That(output).DoesNotContain(",0");
    }

    [Test]
    public async Task MarkdownFormat_UnderCommaDecimalCulture_UsesDotDecimals()
    {
        var md = CultureScope.RenderWithCommaDecimal(() => MarkdownFormatter.Format(Reports.Mixed, threshold: 80));

        await Assert.That(md).Contains("**Line coverage:** 58.3% (7/12)");
        await Assert.That(md).DoesNotContain("58,3");
    }

    [Test]
    public async Task MarkdownFormatDiff_UnderCommaDecimalCulture_UsesDotDecimals()
    {
        var diff = CoverageDiff.Compare(
            Reports.Single("a.cs", hit: 5, total: 10),
            Reports.Single("a.cs", hit: 8, total: 10));

        var md = CultureScope.RenderWithCommaDecimal(() => MarkdownFormatter.FormatDiff(diff));

        await Assert.That(md).Contains("**Overall:** 50.0% → 80.0% (+30.0%)");
        await Assert.That(md).DoesNotContain(",0");
    }

    [Test]
    public async Task JsonFormat_UnderCommaDecimalCulture_ParsesAndKeepsDotDecimals()
    {
        // Utf8JsonWriter is invariant by construction — this pins the whole path anyway,
        // so a future rewrite through string formatting cannot regress silently.
        var json = CultureScope.RenderWithCommaDecimal(() => JsonFormatter.Format(Reports.Mixed));

        var summary = JsonDocument.Parse(json).RootElement.GetProperty("summary");
        await Assert.That(summary.GetProperty("lineRate").GetDouble()).IsEqualTo(58.33);
        await Assert.That(json).DoesNotContain("58,33");
    }
}