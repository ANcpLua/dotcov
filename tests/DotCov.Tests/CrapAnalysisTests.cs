using System.Collections.ObjectModel;
using Xunit;

namespace DotCov.Tests;

/// <summary>
/// Pins the CRAP formula, the at-threshold-passes gate boundary, and the compiler-mangling
/// normalization table (async state machines, lambdas, local functions, generics, accessors,
/// constructors) — plus the two honesty channels: unscored methods and unmatched metrics
/// members are listed, never silently dropped.
/// </summary>
public sealed class CrapAnalysisTests
{
    // ── Formula: hand-computed values ─────────────────────────────────────────

    [Theory]
    [InlineData(5, 0.0, 30.0)]    // 5²·1³ + 5      = 30 (comp², not comp³ — 5³+5 would be 130)
    [InlineData(5, 1.0, 5.0)]     // 5²·0³ + 5      = 5: full coverage always scores comp
    [InlineData(1, 0.0, 2.0)]     // 1²·1³ + 1      = 2: the floor for uncovered code
    [InlineData(2, 0.0, 6.0)]     // 2²·1³ + 2      = 6: exactly the default threshold
    [InlineData(10, 0.5, 22.5)]   // 100·0.125 + 10 = 22.5
    [InlineData(1, 1.0, 1.0)]     // the global minimum
    public void Score_MatchesHandComputedValues(int comp, double cov, double expected)
    {
        Assert.Equal(expected, CrapAnalysis.Score(comp, cov), precision: 12);
    }

    // ── Gate boundary: at-threshold PASSES, strictly-above fails ──────────────

    [Fact]
    public void Evaluate_ScoreExactlyAtThreshold_Passes()
    {
        // comp 2, cov 0 → exactly 6.0 against --max-crap 6: the gate fires only strictly
        // above the threshold, consistent with check's at-threshold-passes semantics.
        var report = CrapAnalysis.Analyze([Uncovered("MyApp.A", "M", complexity: 2)]);

        var gate = report.Evaluate(6);

        Assert.Equal(GateOutcome.Pass, gate.Outcome);
        Assert.True(gate.IsPass);
        Assert.Equal(6.0, gate.WorstScore);
        Assert.StartsWith("PASS:", gate.ToString());
    }

    [Fact]
    public void Evaluate_ScoreStrictlyAboveThreshold_Fails()
    {
        // comp 3, cov 0 → 12 against --max-crap 6.
        var report = CrapAnalysis.Analyze([Uncovered("MyApp.A", "M", complexity: 3)]);

        var gate = report.Evaluate(6);

        Assert.Equal(GateOutcome.Fail, gate.Outcome);
        Assert.Equal(1, gate.AboveThreshold);
        Assert.Equal(12.0, gate.WorstScore);
        Assert.StartsWith("FAIL:", gate.ToString());
    }

    [Fact]
    public void Evaluate_ThresholdEqualToComputedScore_AbsorbsFloatNoise()
    {
        // A threshold set to the mathematically-equal value of a score must pass even when the
        // binary double landed a ulp off — same epsilon policy as GateResult.MeetsThreshold
        // (GateResult.RateEpsilon owns it).
        var mc = Method("MyApp.A", "M", "(System.Int32)", complexity: 3,
            lines: [(1, 1), (2, 0), (3, 0)]);   // cov = 1/3
        var report = CrapAnalysis.Analyze([mc]);
        var score = CrapAnalysis.Score(3, 1.0 / 3);

        Assert.Equal(GateOutcome.Pass, report.Evaluate(score).Outcome);
    }

    [Fact]
    public void Evaluate_JustBelowScore_StillFails()
    {
        // The epsilon absorbs float noise, not real differences.
        var report = CrapAnalysis.Analyze([Uncovered("MyApp.A", "M", complexity: 3)]);   // 12.0

        Assert.Equal(GateOutcome.Fail, report.Evaluate(11.999).Outcome);
    }

    [Fact]
    public void Evaluate_NoMethodData_IsNoDataNotPass()
    {
        var gate = CrapAnalysis.Analyze([]).Evaluate(6);

        Assert.Equal(GateOutcome.NoData, gate.Outcome);
        Assert.False(gate.IsPass);
        Assert.StartsWith("NODATA:", gate.ToString());
    }

    [Fact]
    public void Evaluate_MethodsButNoComplexity_IsNoDataMentioningMetrics()
    {
        var gate = CrapAnalysis.Analyze([Method("MyApp.A", "M", "()", complexity: null, lines: [(1, 1)])])
            .Evaluate(6);

        Assert.Equal(GateOutcome.NoData, gate.Outcome);
        Assert.Contains("--metrics", gate.Reason);
    }

    // ── Normalization table ───────────────────────────────────────────────────

    [Fact]
    public void AsyncStateMachine_MoveNext_FoldsToOriginMethod()
    {
        var mc = Method("MyApp.Calculator/<AddAsync>d__3", "MoveNext", "()", complexity: 4,
            lines: [(10, 1), (11, 0)]);

        var report = CrapAnalysis.Analyze([mc]);

        var m = Assert.Single(report.Methods);
        Assert.Equal("MyApp.Calculator.AddAsync", m.Method);
        Assert.Equal(4, m.Complexity);
        Assert.Equal(0.5, m.Coverage);
    }

    [Fact]
    public void StateMachineInfrastructure_SetStateMachineAndCtor_AreDropped()
    {
        var report = CrapAnalysis.Analyze([
            Method("MyApp.C/<M>d__0", "SetStateMachine", "(System.Runtime.CompilerServices.IAsyncStateMachine)",
                complexity: 1, lines: [(1, 1)]),
            Method("MyApp.C/<M>d__0", ".ctor", "()", complexity: 1, lines: [(1, 1)]),
        ]);

        Assert.Empty(report.Methods);
        Assert.Empty(report.Unscored);   // infrastructure, not an unscored source method
    }

    [Fact]
    public void LambdaInDisplayClass_FoldsAndMergesIntoOriginMethod()
    {
        // The origin method and its captured lambda: folding merges the lambda's lines into
        // UseLambda so cov spans the same code Roslyn's complexity would count.
        var origin = Method("MyApp.C", "UseLambda", "(System.Int32)", complexity: 1, lines: [(5, 1)]);
        var lambda = Method("MyApp.C/<>c__DisplayClass0_0", "<UseLambda>b__0", "(System.Int32)",
            complexity: 2, lines: [(6, 0)]);

        var report = CrapAnalysis.Analyze([origin, lambda]);

        var m = Assert.Single(report.Methods);
        Assert.Equal("MyApp.C.UseLambda", m.Method);
        Assert.Equal(0.5, m.Coverage);           // (5:hit + 6:miss) / 2
        Assert.Equal(2, m.Complexity);           // Math.Max of the folded IL methods
    }

    [Fact]
    public void LocalFunction_MangledName_FoldsToOriginMethod()
    {
        var origin = Method("MyApp.C", "UseLocal", "(System.Int32)", complexity: 1, lines: [(8, 1)]);
        var local = Method("MyApp.C", "<UseLocal>g__Local|0_0", "(System.Int32)", complexity: 3, lines: [(9, 1)]);

        var report = CrapAnalysis.Analyze([origin, local]);

        var m = Assert.Single(report.Methods);
        Assert.Equal("MyApp.C.UseLocal", m.Method);
        Assert.Equal(3, m.Complexity);
        Assert.Equal(1.0, m.Coverage);
    }

    [Fact]
    public void GenericType_AritySuffixStripped()
    {
        var report = CrapAnalysis.Analyze([Method("MyApp.Stack`1", "Push", "(T)", complexity: 1, lines: [(1, 1)])]);

        Assert.Equal("MyApp.Stack.Push", Assert.Single(report.Methods).Method);
    }

    [Fact]
    public void NestedType_SlashBecomesDot()
    {
        var report = CrapAnalysis.Analyze([Method("MyApp.Outer/Inner", "M", "()", complexity: 1, lines: [(1, 1)])]);

        Assert.Equal("MyApp.Outer.Inner.M", Assert.Single(report.Methods).Method);
    }

    // ── Complexity resolution ─────────────────────────────────────────────────

    [Fact]
    public void EmbeddedComplexity_WinsOverMetricsFile()
    {
        var mc = Method("MyApp.A", "M", "(System.Int32)", complexity: 2, lines: [(1, 1)]);
        var metrics = new[] { Member("MyApp.A", "M", CodeMetricsMemberKind.Method, arity: 1, complexity: 9) };

        var m = Assert.Single(CrapAnalysis.Analyze([mc], metrics).Methods);

        Assert.Equal(2, m.Complexity);
        Assert.Equal(CrapComplexitySource.CoverageReport, m.ComplexitySource);
    }

    [Fact]
    public void MetricsFile_FillsInWhenCoverageHasNoComplexity()
    {
        var mc = Method("MyApp.A", "M", "(System.Int32)", complexity: null, lines: [(1, 0)]);
        var metrics = new[] { Member("MyApp.A", "M", CodeMetricsMemberKind.Method, arity: 1, complexity: 5) };

        var report = CrapAnalysis.Analyze([mc], metrics);

        var m = Assert.Single(report.Methods);
        Assert.Equal(5, m.Complexity);
        Assert.Equal(CrapComplexitySource.MetricsFile, m.ComplexitySource);
        Assert.Equal(30.0, m.Score);   // comp 5, cov 0
        Assert.Empty(report.UnmatchedMetricsMembers);
    }

    [Fact]
    public void Overloads_DisambiguatedByArity()
    {
        var oneArg = Method("MyApp.A", "M", "(System.Int32)", complexity: null, lines: [(1, 1)]);
        var twoArg = Method("MyApp.A", "M", "(System.Int32,System.Int32)", complexity: null, lines: [(5, 1)]);
        var metrics = new[]
        {
            Member("MyApp.A", "M", CodeMetricsMemberKind.Method, arity: 1, complexity: 2),
            Member("MyApp.A", "M", CodeMetricsMemberKind.Method, arity: 2, complexity: 7),
        };

        var report = CrapAnalysis.Analyze([oneArg, twoArg], metrics);

        Assert.Equal(2, report.Methods.Count);
        Assert.Contains(report.Methods, m => m.Complexity == 2);
        Assert.Contains(report.Methods, m => m.Complexity == 7);
        Assert.Empty(report.UnmatchedMetricsMembers);
    }

    [Fact]
    public void Accessor_GetPrefix_MatchesMetricsAccessorEntry()
    {
        var mc = Method("MyApp.A", "get_Value", "()", complexity: null, lines: [(3, 1)]);
        var metrics = new[] { Member("MyApp.A", "get_Value", CodeMetricsMemberKind.Accessor, arity: null, complexity: 1) };

        var m = Assert.Single(CrapAnalysis.Analyze([mc], metrics).Methods);

        Assert.Equal(1, m.Complexity);
    }

    [Fact]
    public void Accessor_FallsBackToPropertyAggregate_WhenNoAccessorEntries()
    {
        // Older Metrics versions emit no <Accessors>: the property aggregate is the coarser
        // honest fallback.
        var mc = Method("MyApp.A", "get_Value", "()", complexity: null, lines: [(3, 1)]);
        var metrics = new[] { Member("MyApp.A", "Value", CodeMetricsMemberKind.Property, arity: null, complexity: 2) };

        var m = Assert.Single(CrapAnalysis.Analyze([mc], metrics).Methods);

        Assert.Equal(2, m.Complexity);
    }

    // ── Honesty channels ──────────────────────────────────────────────────────

    [Fact]
    public void MissingMetricsFile_MethodWithoutComplexity_ListedAsUnscored()
    {
        var scored = Method("MyApp.A", "WithComp", "()", complexity: 2, lines: [(1, 1)]);
        var unscored = Method("MyApp.A", "NoComp", "()", complexity: null, lines: [(5, 1)]);

        var report = CrapAnalysis.Analyze([scored, unscored]);

        Assert.Single(report.Methods);
        var u = Assert.Single(report.Unscored);
        Assert.Equal("MyApp.A.NoComp", u.Method);
        Assert.Contains("--metrics", u.Reason);
        // The unscored method never fails the gate — but is never silently dropped either.
        Assert.Equal(GateOutcome.Pass, report.Evaluate(6).Outcome);
    }

    [Fact]
    public void MetricsMemberMatchingNothing_ListedAsUnmatched()
    {
        var mc = Method("MyApp.A", "M", "()", complexity: 1, lines: [(1, 1)]);
        var metrics = new[]
        {
            Member("MyApp.A", "Ghost", CodeMetricsMemberKind.Method, arity: 0, complexity: 3,
                display: "void A.Ghost()"),
            // Aggregates are complexity roll-ups, not matchable members — never listed.
            Member("MyApp.A", "Field", CodeMetricsMemberKind.Field, arity: null, complexity: 1),
        };

        var report = CrapAnalysis.Analyze([mc], metrics);

        Assert.Equal(["void A.Ghost()"], report.UnmatchedMetricsMembers);
    }

    [Fact]
    public void MethodWithNoLines_ListedAsUnscored()
    {
        var report = CrapAnalysis.Analyze([Method("MyApp.A", "Empty", "()", complexity: 3, lines: [])]);

        Assert.Empty(report.Methods);
        Assert.Contains("no line data", Assert.Single(report.Unscored).Reason);
    }

    // ── Exclusions ────────────────────────────────────────────────────────────

    [Fact]
    public void ExcludeFiles_SameSemanticsAsReportExclude()
    {
        var methods = new[]
        {
            Method("MyApp.A", "M", "()", complexity: 1, lines: [(1, 1)], file: "src/A.cs"),
            Method("MyApp.Migrations.X", "Up", "()", complexity: 1, lines: [(1, 1)], file: "src/Migrations/X.cs"),
            Method("MyApp.Program", "Main", "()", complexity: 1, lines: [(1, 1)], file: "Program.cs"),
        };

        var filtered = CrapAnalysis.ExcludeFiles(methods, ExclusionRules.WellKnown, keep: ["Program.cs"]);

        Assert.Equal(2, filtered.Count);
        Assert.DoesNotContain(filtered, m => m.File.Contains("Migrations"));
        Assert.Contains(filtered, m => m.File == "Program.cs");   // keep wins, incl. the virtual-root anchor
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static MethodCoverage Uncovered(string className, string methodName, int complexity) =>
        Method(className, methodName, "()", complexity, lines: [(1, 0)]);

    private static MethodCoverage Method(
        string className, string methodName, string signature, int? complexity,
        (int Line, int Hits)[] lines, string file = "src/A.cs")
    {
        var hits = lines.ToDictionary(l => l.Line, l => l.Hits);
        return new MethodCoverage(
            className, methodName, signature, file,
            lines.Length is 0 ? 0 : lines.Min(l => l.Line),
            lines.Length is 0 ? 0 : lines.Max(l => l.Line),
            lines.Count(l => l.Hits > 0), lines.Length, complexity)
        {
            LineHits = new ReadOnlyDictionary<int, int>(hits)
        };
    }

    private static CodeMetricsMember Member(
        string typeName, string memberName, CodeMetricsMemberKind kind, int? arity, int complexity,
        string? display = null) =>
        new(typeName, memberName, kind, arity, complexity, display ?? $"{typeName}.{memberName}");
}
