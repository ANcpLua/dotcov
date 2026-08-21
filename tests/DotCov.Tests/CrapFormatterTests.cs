using System.Globalization;
using System.Text.Json;
using DotCov.Formatters;
using DotCov.Tests.Infrastructure;
using Xunit;

namespace DotCov.Tests;

/// <summary>
/// Pins the CRAP formatters: worst-first ordering, --top display truncation (gate still sees
/// all), the honesty trailers, and — because this output lands in CI logs and PR summaries —
/// invariant numeric formatting under a comma-decimal culture (same policy as
/// <see cref="FormatterCultureTests"/>, and serialized in <c>EnvCollection</c> for the same
/// CurrentCulture-is-thread-state reason).
/// </summary>
[Collection(nameof(EnvCollection))]
public sealed class CrapFormatterTests
{
    private static readonly CrapReport Report = CrapAnalysis.Analyze([
        Method("MyApp.A", "Low", 1, [(1, 1)]),                      // CRAP 1.0
        Method("MyApp.A", "Worst", 5, [(10, 0), (11, 0)]),          // CRAP 30.0
        Method("MyApp.A", "Mid", 3, [(20, 1), (21, 0)]),            // comp 3, cov .5 → 4.125
    ]);

    private static string InCommaDecimalCulture(Func<string> render)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            var commaCulture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            commaCulture.NumberFormat.NumberDecimalSeparator = ",";
            CultureInfo.CurrentCulture = commaCulture;
            return render();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Table_OrdersWorstFirst()
    {
        var gate = Report.Evaluate(6);
        var output = CrapFormatter.Format(Report, gate);

        var worst = output.IndexOf("MyApp.A.Worst", StringComparison.Ordinal);
        var mid = output.IndexOf("MyApp.A.Mid", StringComparison.Ordinal);
        var low = output.IndexOf("MyApp.A.Low", StringComparison.Ordinal);
        Assert.True(worst < mid && mid < low, $"expected worst-first ordering in:\n{output}");
    }

    [Fact]
    public void Table_TopTruncatesDisplay_GateStillSeesAll()
    {
        var gate = Report.Evaluate(6);
        var output = CrapFormatter.Format(Report, gate, top: 1);

        Assert.Contains("MyApp.A.Worst", output);
        Assert.DoesNotContain("MyApp.A.Low", output);
        Assert.Contains("2 more methods below", output);
        Assert.Equal(3, gate.ScoredMethods);   // the gate is computed over the full set
    }

    [Fact]
    public void Table_UnderCommaDecimalCulture_UsesDotDecimals()
    {
        var output = InCommaDecimalCulture(() => CrapFormatter.Format(Report, Report.Evaluate(6)));

        Assert.Contains("30.0", output);
        Assert.Contains("0.0%", output);
        Assert.DoesNotContain("30,0", output);
        Assert.DoesNotContain("0,0%", output);
    }

    [Fact]
    public void Markdown_UnderCommaDecimalCulture_UsesDotDecimals()
    {
        var md = InCommaDecimalCulture(() => CrapFormatter.FormatMarkdown(Report, Report.Evaluate(6)));

        Assert.Contains("| `MyApp.A.Worst` | 5 | 0.0% | 30.0 ❌ |", md);
        Assert.DoesNotContain("30,0", md);
    }

    [Fact]
    public void Markdown_FailBadgeAndBacktickedVerdict_FromSameGate()
    {
        var md = CrapFormatter.FormatMarkdown(Report, Report.Evaluate(6));

        Assert.Contains("## CRAP Report ❌", md);
        Assert.Contains("`FAIL: worst CRAP 30.0 (max 6) - 1 of 3 methods above threshold`", md);
    }

    [Fact]
    public void Markdown_PassBadge_WhenAllUnderThreshold()
    {
        var md = CrapFormatter.FormatMarkdown(Report, Report.Evaluate(50));

        Assert.Contains("## CRAP Report ✅", md);
        Assert.Contains("`PASS:", md);
        Assert.DoesNotContain("❌", md);
    }

    [Fact]
    public void Json_ShapeAndInvariance()
    {
        var json = InCommaDecimalCulture(() => CrapFormatter.FormatJson(Report, Report.Evaluate(6)));

        var root = JsonDocument.Parse(json).RootElement;
        var gate = root.GetProperty("gate");
        Assert.Equal("fail", gate.GetProperty("outcome").GetString());
        Assert.Equal(6, gate.GetProperty("maxCrap").GetDouble());
        Assert.Equal(3, gate.GetProperty("scoredMethods").GetInt32());
        Assert.Equal(1, gate.GetProperty("aboveThreshold").GetInt32());
        Assert.Equal(30.0, gate.GetProperty("worstScore").GetDouble());

        var methods = root.GetProperty("methods").EnumerateArray().ToList();
        Assert.Equal(3, methods.Count);
        var worst = methods[0];   // worst-first in JSON too
        Assert.Equal("MyApp.A.Worst", worst.GetProperty("method").GetString());
        Assert.Equal(5, worst.GetProperty("complexity").GetInt32());
        Assert.Equal(30.0, worst.GetProperty("crap").GetDouble());
        Assert.True(worst.GetProperty("aboveThreshold").GetBoolean());
        Assert.Equal("coverageReport", worst.GetProperty("complexitySource").GetString());

        // Absent key == clean: no unscored/unmatched arrays on a fully scored report.
        Assert.False(root.TryGetProperty("unscored", out _));
        Assert.False(root.TryGetProperty("unmatchedMetricsMembers", out _));
    }

    [Fact]
    public void Json_UnscoredAndUnmatched_PresentWhenNonEmpty()
    {
        var report = CrapAnalysis.Analyze(
            [Method("MyApp.A", "NoComp", null, [(1, 1)])],
            [new CodeMetricsMember("MyApp.B", "Ghost", CodeMetricsMemberKind.Method, 0, 3, "void B.Ghost()")]);

        var json = CrapFormatter.FormatJson(report, report.Evaluate(6));

        var root = JsonDocument.Parse(json).RootElement;
        Assert.Equal("MyApp.A.NoComp",
            root.GetProperty("unscored")[0].GetProperty("method").GetString());
        Assert.Equal("void B.Ghost()",
            root.GetProperty("unmatchedMetricsMembers")[0].GetString());
    }

    [Fact]
    public void Table_ListsUnscoredAndUnmatched_NeverSilentlyDrops()
    {
        var report = CrapAnalysis.Analyze(
            [Method("MyApp.A", "NoComp", null, [(1, 1)])],
            [new CodeMetricsMember("MyApp.B", "Ghost", CodeMetricsMemberKind.Method, 0, 3, "void B.Ghost()")]);

        var output = CrapFormatter.Format(report, report.Evaluate(6));

        Assert.Contains("Unscored (no complexity source): 1", output);
        Assert.Contains("MyApp.A.NoComp", output);
        Assert.Contains("Unmatched metrics members: 1", output);
        Assert.Contains("void B.Ghost()", output);
    }

    private static MethodCoverage Method(string className, string name, int? complexity, (int Line, int Hits)[] lines)
    {
        var hits = lines.ToDictionary(l => l.Line, l => l.Hits);
        return new MethodCoverage(className, name, "()", "src/A.cs",
            lines.Min(l => l.Line), lines.Max(l => l.Line),
            lines.Count(l => l.Hits > 0), lines.Length, complexity)
        {
            LineHits = new System.Collections.ObjectModel.ReadOnlyDictionary<int, int>(hits)
        };
    }
}
