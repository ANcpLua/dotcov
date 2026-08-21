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

    // ── Nested mangled names: async lambdas, top-level statements, local fns ──

    [Fact]
    public void AsyncLambdaStateMachine_InsideDisplayClass_FoldsAndMergesIntoOriginMethod()
    {
        // Real coverlet shape for `async () => …` inside RunAsync: the lambda's state machine
        // nests INSIDE the display class — Ns.Type/<>c/<<RunAsync>b__0_0>d + MoveNext.
        // Regression: first-'>' bracket parsing dropped this entry entirely (it appeared in
        // neither Methods nor Unscored), turning the gate falsely green.
        var origin = Method("Probe.Lib", "RunAsync", "()", complexity: 1, lines: [(5, 1)]);
        var lambda = Method("Probe.Lib/<>c/<<RunAsync>b__0_0>d", "MoveNext", "()", complexity: 6,
            lines: [(10, 0), (11, 0), (12, 0)]);

        var report = CrapAnalysis.Analyze([origin, lambda]);

        var m = Assert.Single(report.Methods);
        Assert.Equal("Probe.Lib.RunAsync", m.Method);
        Assert.Equal(6, m.Complexity);          // Math.Max of origin and folded state machine
        Assert.Equal(0.25, m.Coverage);         // line 5 hit; 10–12 missed
    }

    [Fact]
    public void AsyncLambdaStateMachine_WithoutOriginEntry_StandsAsItsOwnRow_AndFailsGate()
    {
        var lambda = Method("MyApp.Svc/<>c/<<Run>b__2_0>d", "MoveNext", "()", complexity: 9,
            lines: [(20, 0), (21, 0), (22, 0)]);

        var report = CrapAnalysis.Analyze([lambda]);

        var m = Assert.Single(report.Methods);
        Assert.Equal("MyApp.Svc.Run", m.Method);
        Assert.Equal(90.0, m.Score);            // 9²·1³ + 9
        Assert.Equal(GateOutcome.Fail, report.Evaluate(6).Outcome);
    }

    [Fact]
    public void AsyncLambdaStateMachine_InsideCapturingDisplayClass_FoldsToOriginMethod()
    {
        // A capturing async lambda nests its state machine inside <>c__DisplayClass…, not <>c.
        var sm = Method("MyApp.C/<>c__DisplayClass3_0/<<M>b__0>d", "MoveNext", "()",
            complexity: 2, lines: [(7, 1)]);

        Assert.Equal("MyApp.C.M", Assert.Single(CrapAnalysis.Analyze([sm]).Methods).Method);
    }

    [Fact]
    public void AsyncLambdaStateMachine_Infrastructure_IsDropped()
    {
        var report = CrapAnalysis.Analyze([
            Method("MyApp.Svc/<>c/<<Run>b__2_0>d", ".ctor", "()", complexity: 1, lines: [(1, 1)]),
            Method("MyApp.Svc/<>c/<<Run>b__2_0>d", "SetStateMachine",
                "(System.Runtime.CompilerServices.IAsyncStateMachine)", complexity: 1, lines: [(1, 1)]),
        ]);

        Assert.Empty(report.Methods);
        Assert.Empty(report.Unscored);
    }

    [Fact]
    public void TopLevelStatements_AsyncEntryPoint_NormalizesToProgramMain()
    {
        // Program/<<Main>$>d__0 + MoveNext: the '$' suffix and the nested brackets both parse.
        var sm = Method("Program/<<Main>$>d__0", "MoveNext", "()", complexity: 3,
            lines: [(1, 1), (2, 0)]);

        var m = Assert.Single(CrapAnalysis.Analyze([sm]).Methods);
        Assert.Equal("Program.Main", m.Method);
        Assert.Equal(3, m.Complexity);
    }

    [Fact]
    public void TopLevelStatements_SyncEntryPoint_NormalizesToProgramMain()
    {
        var mc = Method("Program", "<Main>$", "(System.String[])", complexity: 1, lines: [(1, 1)]);

        Assert.Equal("Program.Main", Assert.Single(CrapAnalysis.Analyze([mc]).Methods).Method);
    }

    [Fact]
    public void AsyncLocalFunction_StateMachine_FoldsToContainingMethod()
    {
        var sm = Method("MyApp.C/<<M>g__Local|0_0>d", "MoveNext", "()", complexity: 2, lines: [(3, 1)]);

        Assert.Equal("MyApp.C.M", Assert.Single(CrapAnalysis.Analyze([sm]).Methods).Method);
    }

    // ── Same-arity overloads stay distinct ────────────────────────────────────

    [Fact]
    public void SameArityOverloads_StayDistinct_UncoveredOverloadStillFailsGate()
    {
        // Regression: keying logical methods by ARITY merged Frob(Int32) into Frob(String),
        // diluting the uncovered comp-20 overload's CRAP 420 to a passing 23.2.
        var risky = Method("MyApp.Svc", "Frob", "(System.Int32)", complexity: 20,
            lines: [(10, 0), (11, 0), (12, 0), (13, 0), (14, 0)]);
        var covered = Method("MyApp.Svc", "Frob", "(System.String)", complexity: 1,
            lines: [.. Enumerable.Range(30, 20).Select(l => (l, 1))]);

        var report = CrapAnalysis.Analyze([risky, covered]);

        Assert.Equal(2, report.Methods.Count);
        Assert.Contains(report.Methods, m => m is { Complexity: 20, Score: 420.0 });
        Assert.Equal(GateOutcome.Fail, report.Evaluate(30).Outcome);
    }

    [Fact]
    public void FoldedStateMachine_AmbiguousAmongSameArityOverloads_StandsAsItsOwnRow()
    {
        // Two same-arity Run overloads: the folded <Run>d__2 group has no unambiguous origin,
        // so it must not merge into either — it stands as its own row (and can fail the gate).
        var a = Method("MyApp.Calc", "Run", "(System.Int32)", complexity: 1, lines: [(10, 1)]);
        var b = Method("MyApp.Calc", "Run", "(System.String)", complexity: 1, lines: [(20, 1)]);
        var sm = Method("MyApp.Calc/<Run>d__2", "MoveNext", "()", complexity: 5,
            lines: [(30, 0), (31, 0)]);

        var report = CrapAnalysis.Analyze([a, b, sm]);

        Assert.Equal(3, report.Methods.Count);
        Assert.Contains(report.Methods, m => m is { Complexity: 5, Coverage: 0.0, Score: 30.0 });
        Assert.Equal(GateOutcome.Fail, report.Evaluate(6).Outcome);
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
