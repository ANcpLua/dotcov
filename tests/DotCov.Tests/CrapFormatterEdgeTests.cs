using System.Collections.ObjectModel;
using System.Text.Json;
using DotCov.Formatters;

namespace DotCov.Tests;

/// <summary>
/// Edge cases of <see cref="CrapFormatter"/>: an offender's score must never render as a value
/// that reads as at-or-below the maximum it exceeded, method names carrying markdown syntax must
/// not reshape the table, and the display-only switches (<c>top</c>, complexity source) render
/// what they promise.
/// </summary>
public sealed class CrapFormatterEdgeTests
{
    // complexity 6, 12 of 95 lines hit: CRAP = 36 · (83/95)³ + 6 = 30.0086 — above 30 by less
    // than a twentieth, so one-decimal rounding prints "30.0".
    private static readonly CrapReport JustAboveThirty = CrapAnalysis.Analyze([
        Method("MyApp.A", "Risky", 6, hit: 12, total: 95),
    ]);

    [Test]
    public async Task Table_OffenderJustAboveMax_NeverReadsAsAtTheMax()
    {
        var gate = JustAboveThirty.Evaluate(30);

        var output = CrapFormatter.Format(JustAboveThirty, gate);

        await Assert.That(gate.Outcome).IsEqualTo(GateOutcome.Fail);
        await Assert.That(output).Contains("MyApp.A.Risky      6     12.6%      30.1");
        await Assert.That(output).DoesNotContain("30.0");
    }

    [Test]
    public async Task Markdown_OffenderJustAboveMax_NeverReadsAsAtTheMax()
    {
        var gate = JustAboveThirty.Evaluate(30);

        var md = CrapFormatter.FormatMarkdown(JustAboveThirty, gate);

        await Assert.That(md).Contains("| `MyApp.A.Risky` | 6 | 12.6% | 30.1 ❌ |");
    }

    [Test]
    public async Task Markdown_NonOffenderBelowMax_KeepsNearestRounding()
    {
        // Rounding toward the verdict applies to offenders only: a passing 30.0086 against
        // max 31 renders rounded, like GateResult leaves a passing rate unfloored.
        var md = CrapFormatter.FormatMarkdown(JustAboveThirty, JustAboveThirty.Evaluate(31));

        await Assert.That(md).Contains("| `MyApp.A.Risky` | 6 | 12.6% | 30.0 |");
    }

    [Test]
    public async Task Table_OffenderExactlyOnATenth_IsNotRaisedAnotherTenth()
    {
        // complexity 5, coverage 0: CRAP is exactly 30. Against max 29 it exceeds, and its
        // display must stay 30.0 - the epsilon keeps float noise from ceiling it to 30.1.
        var report = CrapAnalysis.Analyze([Method("MyApp.A", "Worst", 5, hit: 0, total: 2)]);

        var output = CrapFormatter.Format(report, report.Evaluate(29), top: null, color: true);

        await Assert.That(output).Contains("\e[31m    30.0\e[39m");
    }

    [Test]
    public async Task Markdown_PipeInMethodName_KeepsTheRowAtFourCells()
    {
        // F# active patterns compile to methods named like "|Even|Odd|"; GFM splits cells on
        // every unescaped pipe, code spans included.
        var report = CrapAnalysis.Analyze([Method("MyApp.Patterns", "|Even|Odd|", 2, hit: 1, total: 1)]);

        var md = CrapFormatter.FormatMarkdown(report, report.Evaluate(30));

        await Assert.That(md).Contains(@"| `MyApp.Patterns.\|Even\|Odd\|` | 2 | 100.0% | 2.0 |");
    }

    [Test]
    public async Task Markdown_TopAtLeastMethodCount_ShowsEveryRowWithoutTruncationNote()
    {
        var report = CrapAnalysis.Analyze([
            Method("MyApp.A", "One", 1, hit: 1, total: 1),
            Method("MyApp.A", "Two", 2, hit: 0, total: 1),
        ]);

        var md = CrapFormatter.FormatMarkdown(report, report.Evaluate(30), top: 2);

        await Assert.That(md).Contains("| `MyApp.A.One` |");
        await Assert.That(md).Contains("| `MyApp.A.Two` |");
        await Assert.That(md).DoesNotContain("more methods below");
    }

    [Test]
    public async Task Json_MetricsSuppliedComplexity_ReportsMetricsFileSource()
    {
        var report = CrapAnalysis.Analyze(
            [Method("MyApp.A", "M", complexity: null, hit: 0, total: 1)],
            [new CodeMetricsMember("MyApp.A", "M", CodeMetricsMemberKind.Method, 0, 5, "void A.M()")]);

        var json = CrapFormatter.FormatJson(report, report.Evaluate(30));

        var method = JsonDocument.Parse(json).RootElement.GetProperty("methods")[0];
        await Assert.That(method.GetProperty("complexity").GetInt32()).IsEqualTo(5);
        await Assert.That(method.GetProperty("complexitySource").GetString()).IsEqualTo("metricsFile");
    }

    [Test]
    public async Task Json_InfiniteMax_RendersDisabledGateWithNullMax()
    {
        // An infinite --max-crap is the documented DISABLED gate. JSON has no Infinity, and
        // Utf8JsonWriter threw on it, so `dotcov crap --max-crap Infinity --format json` died.
        var gate = JustAboveThirty.Evaluate(double.PositiveInfinity);

        var json = CrapFormatter.FormatJson(JustAboveThirty, gate);

        var root = JsonDocument.Parse(json).RootElement.GetProperty("gate");
        await Assert.That(root.GetProperty("outcome").GetString()).IsEqualTo("disabled");
        await Assert.That(root.GetProperty("maxCrap").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(root.GetProperty("reason").GetString()).IsEqualTo("no finite threshold - this gate cannot fail");
        await Assert.That(root.GetProperty("worstScore").GetDouble()).IsEqualTo(30.01);
    }

    [Test]
    public async Task Json_NegativeInfiniteMax_RendersFailingGateWithNullMax()
    {
        var gate = JustAboveThirty.Evaluate(double.NegativeInfinity);

        var json = CrapFormatter.FormatJson(JustAboveThirty, gate);

        var root = JsonDocument.Parse(json).RootElement;
        await Assert.That(root.GetProperty("gate").GetProperty("outcome").GetString()).IsEqualTo("fail");
        await Assert.That(root.GetProperty("gate").GetProperty("maxCrap").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(root.GetProperty("methods")[0].GetProperty("aboveThreshold").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task Json_NotANumberMax_RendersNullMax()
    {
        var gate = new CrapGateResult(GateOutcome.Disabled, double.NaN, 1, 0, 30.0085, "no threshold");

        var json = CrapFormatter.FormatJson(JustAboveThirty, gate);

        await Assert.That(JsonDocument.Parse(json).RootElement.GetProperty("gate").GetProperty("maxCrap").ValueKind)
            .IsEqualTo(JsonValueKind.Null);
    }

    [Test]
    public async Task TableAndMarkdown_InfiniteMax_RenderWithoutOffenders()
    {
        var gate = JustAboveThirty.Evaluate(double.PositiveInfinity);

        var table = CrapFormatter.Format(JustAboveThirty, gate);
        var md = CrapFormatter.FormatMarkdown(JustAboveThirty, gate);

        await Assert.That(table).Contains("MyApp.A.Risky      6     12.6%      30.0");
        await Assert.That(md).Contains("## CRAP Report ⚠️");
        await Assert.That(md).Contains("**Threshold:** max CRAP Infinity");
        await Assert.That(md).Contains("| `MyApp.A.Risky` | 6 | 12.6% | 30.0 |");
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
