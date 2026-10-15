using System.Collections.ObjectModel;
using System.Text.Json;
using DotCov.Formatters;

namespace DotCov.Tests;
/// <summary>
/// Edge cases of <see cref = "CrapFormatter"/>: an offender's score must never render as a value
/// that reads as at-or-below the maximum it exceeded, method names carrying markdown syntax must
/// not reshape the table, and the display-only switches (<c>top</c>, complexity source) render
/// what they promise.
/// </summary>
[TestClass]
public sealed class CrapFormatterEdgeTests
{
    // complexity 6, 12 of 95 lines hit: CRAP = 36 · (83/95)³ + 6 = 30.0086 — above 30 by less
    // than a twentieth, so one-decimal rounding prints "30.0".
    private static readonly CrapReport JustAboveThirty = CrapAnalysis.Analyze([Method("MyApp.A", "Risky", 6, hit: 12, total: 95), ]);
    [TestMethod]
    public void Table_OffenderJustAboveMax_NeverReadsAsAtTheMax()
    {
        var gate = JustAboveThirty.Evaluate(30);
        var output = CrapFormatter.Format(JustAboveThirty, gate);
        Assert.AreEqual(GateOutcome.Fail, gate.Outcome);
        Assert.Contains("MyApp.A.Risky      6     12.6%      30.1", output);
        Assert.DoesNotContain("30.0", output);
    }

    [TestMethod]
    public void Markdown_OffenderJustAboveMax_NeverReadsAsAtTheMax()
    {
        var gate = JustAboveThirty.Evaluate(30);
        var md = CrapFormatter.FormatMarkdown(JustAboveThirty, gate);
        Assert.Contains("| `MyApp.A.Risky` | 6 | 12.6% | 30.1 ❌ |", md);
    }

    [TestMethod]
    public void Markdown_NonOffenderBelowMax_KeepsNearestRounding()
    {
        // Rounding toward the verdict applies to offenders only: a passing 30.0086 against
        // max 31 renders rounded, like GateResult leaves a passing rate unfloored.
        var md = CrapFormatter.FormatMarkdown(JustAboveThirty, JustAboveThirty.Evaluate(31));
        Assert.Contains("| `MyApp.A.Risky` | 6 | 12.6% | 30.0 |", md);
    }

    [TestMethod]
    public void Table_OffenderExactlyOnATenth_IsNotRaisedAnotherTenth()
    {
        // complexity 5, coverage 0: CRAP is exactly 30. Against max 29 it exceeds, and its
        // display must stay 30.0 - the epsilon keeps float noise from ceiling it to 30.1.
        var report = CrapAnalysis.Analyze([Method("MyApp.A", "Worst", 5, hit: 0, total: 2)]);
        var output = CrapFormatter.Format(report, report.Evaluate(29), top: null, color: true);
        Assert.Contains("\e[31m    30.0\e[39m", output);
    }

    [TestMethod]
    public void Markdown_PipeInMethodName_KeepsTheRowAtFourCells()
    {
        // F# active patterns compile to methods named like "|Even|Odd|"; GFM splits cells on
        // every unescaped pipe, code spans included.
        var report = CrapAnalysis.Analyze([Method("MyApp.Patterns", "|Even|Odd|", 2, hit: 1, total: 1)]);
        var md = CrapFormatter.FormatMarkdown(report, report.Evaluate(30));
        Assert.Contains(@"| `MyApp.Patterns.\|Even\|Odd\|` | 2 | 100.0% | 2.0 |", md);
    }

    [TestMethod]
    public void Markdown_TopAtLeastMethodCount_ShowsEveryRowWithoutTruncationNote()
    {
        var report = CrapAnalysis.Analyze([Method("MyApp.A", "One", 1, hit: 1, total: 1), Method("MyApp.A", "Two", 2, hit: 0, total: 1), ]);
        var md = CrapFormatter.FormatMarkdown(report, report.Evaluate(30), top: 2);
        Assert.Contains("| `MyApp.A.One` |", md);
        Assert.Contains("| `MyApp.A.Two` |", md);
        Assert.DoesNotContain("more methods below", md);
    }

    [TestMethod]
    public void Json_MetricsSuppliedComplexity_ReportsMetricsFileSource()
    {
        var report = CrapAnalysis.Analyze([Method("MyApp.A", "M", complexity: null, hit: 0, total: 1)], [new CodeMetricsMember("MyApp.A", "M", CodeMetricsMemberKind.Method, 0, 5, "void A.M()")]);
        var json = CrapFormatter.FormatJson(report, report.Evaluate(30));
        var method = JsonDocument.Parse(json).RootElement.GetProperty("methods")[0];
        Assert.AreEqual(5, method.GetProperty("complexity").GetInt32());
        Assert.AreEqual("metricsFile", method.GetProperty("complexitySource").GetString());
    }

    [TestMethod]
    public void Json_InfiniteMax_RendersDisabledGateWithNullMax()
    {
        // An infinite --max-crap is the documented DISABLED gate. JSON has no Infinity and
        // Utf8JsonWriter throws on it, so the maximum renders as null.
        var gate = JustAboveThirty.Evaluate(double.PositiveInfinity);
        var json = CrapFormatter.FormatJson(JustAboveThirty, gate);
        var root = JsonDocument.Parse(json).RootElement.GetProperty("gate");
        Assert.AreEqual("disabled", root.GetProperty("outcome").GetString());
        Assert.AreEqual(JsonValueKind.Null, root.GetProperty("maxCrap").ValueKind);
        Assert.AreEqual(4, root.GetProperty("exitCode").GetInt32());
        Assert.AreEqual("DISABLED: worst CRAP 30.0 (max Infinity) - no finite threshold - this gate cannot fail", root.GetProperty("reason").GetString());
        Assert.AreEqual(30.01, root.GetProperty("worstScore").GetDouble());
    }

    [TestMethod]
    public void Json_NegativeInfiniteMax_RendersFailingGateWithNullMax()
    {
        var gate = JustAboveThirty.Evaluate(double.NegativeInfinity);
        var json = CrapFormatter.FormatJson(JustAboveThirty, gate);
        var root = JsonDocument.Parse(json).RootElement;
        Assert.AreEqual("fail", root.GetProperty("gate").GetProperty("outcome").GetString());
        Assert.AreEqual(JsonValueKind.Null, root.GetProperty("gate").GetProperty("maxCrap").ValueKind);
        Assert.IsTrue(root.GetProperty("methods")[0].GetProperty("aboveThreshold").GetBoolean());
    }

    [TestMethod]
    public void Json_NotANumberMax_RendersNullMax()
    {
        var gate = new CrapGateResult(GateOutcome.Disabled, double.NaN, 1, 0, 30.0085, "no threshold");
        var json = CrapFormatter.FormatJson(JustAboveThirty, gate);
        Assert.AreEqual(JsonValueKind.Null, JsonDocument.Parse(json).RootElement.GetProperty("gate").GetProperty("maxCrap").ValueKind);
    }

    [TestMethod]
    public void TableAndMarkdown_InfiniteMax_RenderWithoutOffenders()
    {
        var gate = JustAboveThirty.Evaluate(double.PositiveInfinity);
        var table = CrapFormatter.Format(JustAboveThirty, gate);
        var md = CrapFormatter.FormatMarkdown(JustAboveThirty, gate);
        Assert.Contains("MyApp.A.Risky      6     12.6%      30.0", table);
        Assert.Contains("## CRAP Report ⚠️", md);
        Assert.Contains("**Threshold:** max CRAP Infinity", md);
        Assert.Contains("| `MyApp.A.Risky` | 6 | 12.6% | 30.0 |", md);
    }

    private static MethodCoverage Method(string className, string name, int? complexity, int hit, int total)
    {
        var hits = Enumerable.Range(1, total).ToDictionary(line => line, line => line <= hit ? 1 : 0);
        return new MethodCoverage(className, name, "()", "src/A.cs", 1, total, hit, total, complexity)
        {
            LineHits = new ReadOnlyDictionary<int, int>(hits)
        };
    }
}
