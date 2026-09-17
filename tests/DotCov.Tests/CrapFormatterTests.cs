using System.Text.Json;
using DotCov.Formatters;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

/// <summary>
/// Pins the CRAP formatters: worst-first ordering, --top display truncation (gate still sees
/// all), the honesty trailers, and — because this output lands in CI logs and PR summaries —
/// invariant numeric formatting under a comma-decimal culture (same policy as
/// <see cref="FormatterCultureTests"/>, via the same per-test <see cref="CultureScope"/>).
/// </summary>
public sealed class CrapFormatterTests
{
    private static readonly CrapReport Report = CrapAnalysis.Analyze([
        Method("MyApp.A", "Low", 1, [(1, 1)]),                      // CRAP 1.0
        Method("MyApp.A", "Worst", 5, [(10, 0), (11, 0)]),          // CRAP 30.0
        Method("MyApp.A", "Mid", 3, [(20, 1), (21, 0)]),            // comp 3, cov .5 → 4.125
    ]);

    [Test]
    public async Task Table_OrdersWorstFirst()
    {
        var gate = Report.Evaluate(6);
        var output = CrapFormatter.Format(Report, gate);

        var worst = output.IndexOf("MyApp.A.Worst", StringComparison.Ordinal);
        var mid = output.IndexOf("MyApp.A.Mid", StringComparison.Ordinal);
        var low = output.IndexOf("MyApp.A.Low", StringComparison.Ordinal);
        await Assert.That(worst < mid && mid < low).IsTrue().Because($"expected worst-first ordering in:\n{output}");
    }

    [Test]
    public async Task Table_TopTruncatesDisplay_GateStillSeesAll()
    {
        var gate = Report.Evaluate(6);
        var output = CrapFormatter.Format(Report, gate, top: 1);

        await Assert.That(output).Contains("MyApp.A.Worst");
        await Assert.That(output).DoesNotContain("MyApp.A.Low");
        await Assert.That(output).Contains("2 more methods below");
        await Assert.That(gate.ScoredMethods).IsEqualTo(3);   // the gate is computed over the full set
    }

    [Test]
    public async Task Table_UnderCommaDecimalCulture_UsesDotDecimals()
    {
        var output = CultureScope.RenderWithCommaDecimal(() => CrapFormatter.Format(Report, Report.Evaluate(6)));

        await Assert.That(output).Contains("30.0");
        await Assert.That(output).Contains("0.0%");
        await Assert.That(output).DoesNotContain("30,0");
        await Assert.That(output).DoesNotContain("0,0%");
    }

    [Test]
    public async Task Markdown_UnderCommaDecimalCulture_UsesDotDecimals()
    {
        var md = CultureScope.RenderWithCommaDecimal(() => CrapFormatter.FormatMarkdown(Report, Report.Evaluate(6)));

        await Assert.That(md).Contains("| `MyApp.A.Worst` | 5 | 0.0% | 30.0 ❌ |");
        await Assert.That(md).DoesNotContain("30,0");
    }

    [Test]
    public async Task Markdown_FailBadgeAndBacktickedVerdict_FromSameGate()
    {
        var md = CrapFormatter.FormatMarkdown(Report, Report.Evaluate(6));

        await Assert.That(md).Contains("## CRAP Report ❌");
        await Assert.That(md).Contains("`FAIL: worst CRAP 30.0 (max 6) - 1 of 3 methods above threshold`");
    }

    [Test]
    public async Task Markdown_PassBadge_WhenAllUnderThreshold()
    {
        var md = CrapFormatter.FormatMarkdown(Report, Report.Evaluate(50));

        await Assert.That(md).Contains("## CRAP Report ✅");
        await Assert.That(md).Contains("`PASS:");
        await Assert.That(md).DoesNotContain("❌");
    }

    [Test]
    public async Task Markdown_NoData_WarnBadgeAndBlockquoteVerdict()
    {
        // No scorable methods: ⚠️ badge, a blockquote naming the reason, and no table at all.
        var report = CrapAnalysis.Analyze([Method("MyApp.A", "NoComp", null, [(1, 1)])]);

        var md = CrapFormatter.FormatMarkdown(report, report.Evaluate(6));

        await Assert.That(md).Contains("## CRAP Report ⚠️");
        await Assert.That(md).Contains("> **No verdict:**");
        await Assert.That(md).Contains("--metrics");
        await Assert.That(md).DoesNotContain("| Method |");
        await Assert.That(md).Contains("`NODATA:");
    }

    [Test]
    public async Task Markdown_TopTruncatesDisplay_NotesGateEvaluatesAll()
    {
        var md = CrapFormatter.FormatMarkdown(Report, Report.Evaluate(6), top: 1);

        await Assert.That(md).Contains("| `MyApp.A.Worst` |");
        await Assert.That(md).DoesNotContain("MyApp.A.Low");
        await Assert.That(md).Contains("2 more methods below (top 1 shown; the gate evaluates all)");
    }

    [Test]
    public async Task Markdown_ListsUnscoredAndUnmatched_NeverSilentlyDrops()
    {
        // Same honesty channels as the table formatter, in PR-summary form.
        var report = CrapAnalysis.Analyze(
            [Method("MyApp.A", "NoComp", null, [(1, 1)])],
            [new CodeMetricsMember("MyApp.B", "Ghost", CodeMetricsMemberKind.Method, 0, 3, "void B.Ghost()")]);

        var md = CrapFormatter.FormatMarkdown(report, report.Evaluate(6));

        await Assert.That(md).Contains("### Unscored methods (no complexity source)");
        await Assert.That(md).Contains("- `MyApp.A.NoComp` — no matching member in the metrics file");
        await Assert.That(md).Contains("### Unmatched metrics members");
        await Assert.That(md).Contains("- `void B.Ghost()`");
    }

    [Test]
    public async Task Json_ShapeAndInvariance()
    {
        var json = CultureScope.RenderWithCommaDecimal(() => CrapFormatter.FormatJson(Report, Report.Evaluate(6)));

        var root = JsonDocument.Parse(json).RootElement;
        var gate = root.GetProperty("gate");
        await Assert.That(gate.GetProperty("outcome").GetString()).IsEqualTo("fail");
        await Assert.That(gate.GetProperty("maxCrap").GetDouble()).IsEqualTo(6);
        await Assert.That(gate.GetProperty("scoredMethods").GetInt32()).IsEqualTo(3);
        await Assert.That(gate.GetProperty("aboveThreshold").GetInt32()).IsEqualTo(1);
        await Assert.That(gate.GetProperty("worstScore").GetDouble()).IsEqualTo(30.0);

        var methods = root.GetProperty("methods").EnumerateArray().ToList();
        await Assert.That(methods.Count).IsEqualTo(3);
        var worst = methods[0];   // worst-first in JSON too
        await Assert.That(worst.GetProperty("method").GetString()).IsEqualTo("MyApp.A.Worst");
        await Assert.That(worst.GetProperty("complexity").GetInt32()).IsEqualTo(5);
        await Assert.That(worst.GetProperty("crap").GetDouble()).IsEqualTo(30.0);
        await Assert.That(worst.GetProperty("aboveThreshold").GetBoolean()).IsTrue();
        await Assert.That(worst.GetProperty("complexitySource").GetString()).IsEqualTo("coverageReport");

        // Absent key == clean: no unscored/unmatched arrays on a fully scored report.
        await Assert.That(root.TryGetProperty("unscored", out _)).IsFalse();
        await Assert.That(root.TryGetProperty("unmatchedMetricsMembers", out _)).IsFalse();
    }

    [Test]
    public async Task Json_UnscoredAndUnmatched_PresentWhenNonEmpty()
    {
        var report = CrapAnalysis.Analyze(
            [Method("MyApp.A", "NoComp", null, [(1, 1)])],
            [new CodeMetricsMember("MyApp.B", "Ghost", CodeMetricsMemberKind.Method, 0, 3, "void B.Ghost()")]);

        var json = CrapFormatter.FormatJson(report, report.Evaluate(6));

        var root = JsonDocument.Parse(json).RootElement;
        await Assert.That(root.GetProperty("unscored")[0].GetProperty("method").GetString()).IsEqualTo("MyApp.A.NoComp");
        await Assert.That(root.GetProperty("unmatchedMetricsMembers")[0].GetString()).IsEqualTo("void B.Ghost()");
    }

    [Test]
    public async Task Table_ListsUnscoredAndUnmatched_NeverSilentlyDrops()
    {
        var report = CrapAnalysis.Analyze(
            [Method("MyApp.A", "NoComp", null, [(1, 1)])],
            [new CodeMetricsMember("MyApp.B", "Ghost", CodeMetricsMemberKind.Method, 0, 3, "void B.Ghost()")]);

        var output = CrapFormatter.Format(report, report.Evaluate(6));

        await Assert.That(output).Contains("Unscored (no complexity source): 1");
        await Assert.That(output).Contains("MyApp.A.NoComp");
        await Assert.That(output).Contains("Unmatched metrics members: 1");
        await Assert.That(output).Contains("void B.Ghost()");
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