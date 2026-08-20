using System.Globalization;
using System.Text.Json;
using DotCov.Formatters;
using DotCov.Tests.Infrastructure;
using Xunit;

namespace DotCov.Tests;

/// <summary>
/// Formatter output lands in CI logs, PR summaries, and machine-parsed JSON, so its shape
/// must not follow the host locale (a de-AT host writes 58,3 for 58.3 under current-culture
/// formatting). Lives in the serialized <c>EnvCollection</c>: CurrentCulture is thread
/// state, so these tests must not interleave with parallel tests on shared pool threads.
/// </summary>
[Collection(nameof(EnvCollection))]
public sealed class FormatterCultureTests
{
    private static string InCommaDecimalCulture(Func<string> render)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-AT");
            return render();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void TableFormat_UnderCommaDecimalCulture_UsesDotDecimals()
    {
        var output = InCommaDecimalCulture(() => TableFormatter.Format(Reports.Mixed));

        Assert.Contains("58.3%", output); // TOTAL: 7/12 lines
        Assert.DoesNotContain("58,3", output);
    }

    [Fact]
    public void TableFormatDiff_UnderCommaDecimalCulture_UsesDotDecimals()
    {
        var diff = CoverageDiff.Compare(
            Reports.Single("a.cs", hit: 5, total: 10),
            Reports.Single("a.cs", hit: 8, total: 10));

        var output = InCommaDecimalCulture(() => TableFormatter.FormatDiff(diff));

        Assert.Contains("50.0%", output);
        Assert.Contains("80.0%", output);
        Assert.Contains("30.0%", output); // delta column, invariant-formatted
        Assert.DoesNotContain(",0", output);
    }

    [Fact]
    public void MarkdownFormat_UnderCommaDecimalCulture_UsesDotDecimals()
    {
        var md = InCommaDecimalCulture(() => MarkdownFormatter.Format(Reports.Mixed, threshold: 80));

        Assert.Contains("**Line coverage:** 58.3% (7/12)", md);
        Assert.DoesNotContain("58,3", md);
    }

    [Fact]
    public void MarkdownFormatDiff_UnderCommaDecimalCulture_UsesDotDecimals()
    {
        var diff = CoverageDiff.Compare(
            Reports.Single("a.cs", hit: 5, total: 10),
            Reports.Single("a.cs", hit: 8, total: 10));

        var md = InCommaDecimalCulture(() => MarkdownFormatter.FormatDiff(diff));

        Assert.Contains("**Overall:** 50.0% → 80.0% (+30.0%)", md);
        Assert.DoesNotContain(",0", md);
    }

    [Fact]
    public void JsonFormat_UnderCommaDecimalCulture_ParsesAndKeepsDotDecimals()
    {
        // Utf8JsonWriter is invariant by construction — this pins the whole path anyway,
        // so a future rewrite through string formatting cannot regress silently.
        var json = InCommaDecimalCulture(() => JsonFormatter.Format(Reports.Mixed));

        var summary = JsonDocument.Parse(json).RootElement.GetProperty("summary");
        Assert.Equal(58.33, summary.GetProperty("lineRate").GetDouble());
        Assert.DoesNotContain("58,33", json);
    }
}
