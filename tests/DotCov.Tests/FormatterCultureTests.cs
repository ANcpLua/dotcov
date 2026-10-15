using System.Text.Json;
using DotCov.Formatters;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;
/// <summary>
/// Formatter output lands in CI logs, PR summaries, and machine-parsed JSON, so its shape
/// must not follow the host locale (a de-AT host writes 58,3 for 58.3 under current-culture
/// formatting). CurrentCulture is thread state: each test wraps its synchronous render in a
/// <see cref = "CultureScope"/> and restores the culture before returning.
/// </summary>
[TestClass]
public sealed class FormatterCultureTests
{
    [TestMethod]
    public void TableFormat_UnderCommaDecimalCulture_UsesDotDecimals()
    {
        var output = CultureScope.RenderWithCommaDecimal(() => TableFormatter.Format(Reports.Mixed));
        Assert.Contains("58.3%", output); // TOTAL: 7/12 lines
        Assert.DoesNotContain("58,3", output);
    }

    [TestMethod]
    public void TableFormatDiff_UnderCommaDecimalCulture_UsesDotDecimals()
    {
        var diff = CoverageDiff.Compare(Reports.Single("a.cs", hit: 5, total: 10), Reports.Single("a.cs", hit: 8, total: 10));
        var output = CultureScope.RenderWithCommaDecimal(() => TableFormatter.FormatDiff(diff));
        Assert.Contains("50.0%", output);
        Assert.Contains("80.0%", output);
        Assert.Contains("30.0%", output); // delta column, invariant-formatted
        Assert.DoesNotContain(",0", output);
    }

    [TestMethod]
    public void MarkdownFormat_UnderCommaDecimalCulture_UsesDotDecimals()
    {
        var md = CultureScope.RenderWithCommaDecimal(() => MarkdownFormatter.Format(Reports.Mixed, threshold: 80));
        Assert.Contains("**Line coverage:** 58.3% (7/12)", md);
        Assert.DoesNotContain("58,3", md);
    }

    [TestMethod]
    public void MarkdownFormatDiff_UnderCommaDecimalCulture_UsesDotDecimals()
    {
        var diff = CoverageDiff.Compare(Reports.Single("a.cs", hit: 5, total: 10), Reports.Single("a.cs", hit: 8, total: 10));
        var md = CultureScope.RenderWithCommaDecimal(() => MarkdownFormatter.FormatDiff(diff));
        Assert.Contains("**Overall:** 50.0% → 80.0% (+30.0%)", md);
        Assert.DoesNotContain(",0", md);
    }

    [TestMethod]
    public void JsonFormat_UnderCommaDecimalCulture_ParsesAndKeepsDotDecimals()
    {
        // Utf8JsonWriter is invariant by construction — this pins the whole path anyway,
        // so a future rewrite through string formatting cannot regress silently.
        var json = CultureScope.RenderWithCommaDecimal(() => JsonFormatter.Format(Reports.Mixed));
        var summary = JsonDocument.Parse(json).RootElement.GetProperty("summary");
        Assert.AreEqual(58.33, summary.GetProperty("lineRate").GetDouble());
        Assert.DoesNotContain("58,33", json);
    }
}
