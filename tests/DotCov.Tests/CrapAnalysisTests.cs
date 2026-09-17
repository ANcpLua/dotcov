using TUnit.Assertions.Enums;
using System.Collections.ObjectModel;

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
    [Test]
    [Arguments(5, 0.0, 30.0)]    // 5²·1³ + 5      = 30 (comp², not comp³ — 5³+5 would be 130)
    [Arguments(5, 1.0, 5.0)]     // 5²·0³ + 5      = 5: full coverage always scores comp
    [Arguments(1, 0.0, 2.0)]     // 1²·1³ + 1      = 2: the floor for uncovered code
    [Arguments(2, 0.0, 6.0)]     // 2²·1³ + 2      = 6: exactly the default threshold
    [Arguments(10, 0.5, 22.5)]   // 100·0.125 + 10 = 22.5
    [Arguments(1, 1.0, 1.0)]     // the global minimum
    public async Task Score_MatchesHandComputedValues(int comp, double cov, double expected)
    {
        await Assert.That(CrapAnalysis.Score(comp, cov)).IsEqualTo(expected).Within(1e-12);
    }

    // ── Gate boundary: at-threshold PASSES, strictly-above fails ──────────────

    [Test]
    public async Task Evaluate_ScoreExactlyAtThreshold_Passes()
    {
        // comp 2, cov 0 → exactly 6.0 against --max-crap 6: the gate fires only strictly
        // above the threshold, consistent with check's at-threshold-passes semantics.
        var report = CrapAnalysis.Analyze([Uncovered("MyApp.A", "M", complexity: 2)]);

        var gate = report.Evaluate(6);

        await Assert.That(gate.Outcome).IsEqualTo(GateOutcome.Pass);
        await Assert.That(gate.IsPass).IsTrue();
        await Assert.That(gate.WorstScore).IsEqualTo(6.0);
        await Assert.That(gate.ToString()).StartsWith("PASS:");
    }

    [Test]
    public async Task Evaluate_ScoreStrictlyAboveThreshold_Fails()
    {
        // comp 3, cov 0 → 12 against --max-crap 6.
        var report = CrapAnalysis.Analyze([Uncovered("MyApp.A", "M", complexity: 3)]);

        var gate = report.Evaluate(6);

        await Assert.That(gate.Outcome).IsEqualTo(GateOutcome.Fail);
        await Assert.That(gate.AboveThreshold).IsEqualTo(1);
        await Assert.That(gate.WorstScore).IsEqualTo(12.0);
        await Assert.That(gate.ToString()).StartsWith("FAIL:");
    }

    [Test]
    public async Task Evaluate_ThresholdEqualToComputedScore_AbsorbsFloatNoise()
    {
        // A threshold set to the mathematically-equal value of a score must pass even when the
        // binary double landed a ulp off — same epsilon policy as GateResult.MeetsThreshold
        // (GateResult.RateEpsilon owns it).
        var mc = Method("MyApp.A", "M", "(System.Int32)", complexity: 3,
            lines: [(1, 1), (2, 0), (3, 0)]);   // cov = 1/3
        var report = CrapAnalysis.Analyze([mc]);
        var score = CrapAnalysis.Score(3, 1.0 / 3);

        await Assert.That(report.Evaluate(score).Outcome).IsEqualTo(GateOutcome.Pass);
    }

    [Test]
    public async Task Evaluate_JustBelowScore_StillFails()
    {
        // The epsilon absorbs float noise, not real differences.
        var report = CrapAnalysis.Analyze([Uncovered("MyApp.A", "M", complexity: 3)]);   // 12.0

        await Assert.That(report.Evaluate(11.999).Outcome).IsEqualTo(GateOutcome.Fail);
    }

    [Test]
    public async Task Evaluate_NoMethodData_IsNoDataNotPass()
    {
        var gate = CrapAnalysis.Analyze([]).Evaluate(6);

        await Assert.That(gate.Outcome).IsEqualTo(GateOutcome.NoData);
        await Assert.That(gate.IsPass).IsFalse();
        await Assert.That(gate.ToString()).StartsWith("NODATA:");
    }

    [Test]
    public async Task Evaluate_MethodsButNoComplexity_IsNoDataMentioningMetrics()
    {
        var gate = CrapAnalysis.Analyze([Method("MyApp.A", "M", "()", complexity: null, lines: [(1, 1)])])
            .Evaluate(6);

        await Assert.That(gate.Outcome).IsEqualTo(GateOutcome.NoData);
        await Assert.That(gate.Reason).Contains("--metrics");
    }

    // ── Normalization table ───────────────────────────────────────────────────

    [Test]
    public async Task AsyncStateMachine_MoveNext_FoldsToOriginMethod()
    {
        var mc = Method("MyApp.Calculator/<AddAsync>d__3", "MoveNext", "()", complexity: 4,
            lines: [(10, 1), (11, 0)]);

        var report = CrapAnalysis.Analyze([mc]);

        var m = await Assert.That(report.Methods).HasSingleItem();
        await Assert.That(m.Method).IsEqualTo("MyApp.Calculator.AddAsync");
        await Assert.That(m.Complexity).IsEqualTo(4);
        await Assert.That(m.Coverage).IsEqualTo(0.5);
    }

    [Test]
    public async Task StateMachineInfrastructure_SetStateMachineAndCtor_AreDropped()
    {
        var report = CrapAnalysis.Analyze([
            Method("MyApp.C/<M>d__0", "SetStateMachine", "(System.Runtime.CompilerServices.IAsyncStateMachine)",
                complexity: 1, lines: [(1, 1)]),
            Method("MyApp.C/<M>d__0", ".ctor", "()", complexity: 1, lines: [(1, 1)]),
        ]);

        await Assert.That(report.Methods).IsEmpty();
        await Assert.That(report.Unscored).IsEmpty();   // infrastructure, not an unscored source method
    }

    [Test]
    public async Task LambdaInDisplayClass_FoldsAndMergesIntoOriginMethod()
    {
        // The origin method and its captured lambda: folding merges the lambda's lines into
        // UseLambda so cov spans the same code Roslyn's complexity would count.
        var origin = Method("MyApp.C", "UseLambda", "(System.Int32)", complexity: 1, lines: [(5, 1)]);
        var lambda = Method("MyApp.C/<>c__DisplayClass0_0", "<UseLambda>b__0", "(System.Int32)",
            complexity: 2, lines: [(6, 0)]);

        var report = CrapAnalysis.Analyze([origin, lambda]);

        var m = await Assert.That(report.Methods).HasSingleItem();
        await Assert.That(m.Method).IsEqualTo("MyApp.C.UseLambda");
        await Assert.That(m.Coverage).IsEqualTo(0.5);           // (5:hit + 6:miss) / 2
        await Assert.That(m.Complexity).IsEqualTo(2);           // Math.Max of the folded IL methods
    }

    [Test]
    public async Task LocalFunction_MangledName_FoldsToOriginMethod()
    {
        var origin = Method("MyApp.C", "UseLocal", "(System.Int32)", complexity: 1, lines: [(8, 1)]);
        var local = Method("MyApp.C", "<UseLocal>g__Local|0_0", "(System.Int32)", complexity: 3, lines: [(9, 1)]);

        var report = CrapAnalysis.Analyze([origin, local]);

        var m = await Assert.That(report.Methods).HasSingleItem();
        await Assert.That(m.Method).IsEqualTo("MyApp.C.UseLocal");
        await Assert.That(m.Complexity).IsEqualTo(3);
        await Assert.That(m.Coverage).IsEqualTo(1.0);
    }

    [Test]
    public async Task GenericType_AritySuffixStripped()
    {
        var report = CrapAnalysis.Analyze([Method("MyApp.Stack`1", "Push", "(T)", complexity: 1, lines: [(1, 1)])]);

        await Assert.That(report.Methods.Single().Method).IsEqualTo("MyApp.Stack.Push");
    }

    [Test]
    public async Task NestedType_SlashBecomesDot()
    {
        var report = CrapAnalysis.Analyze([Method("MyApp.Outer/Inner", "M", "()", complexity: 1, lines: [(1, 1)])]);

        await Assert.That(report.Methods.Single().Method).IsEqualTo("MyApp.Outer.Inner.M");
    }

    // ── Nested mangled names: async lambdas, top-level statements, local fns ──

    [Test]
    public async Task AsyncLambdaStateMachine_InsideDisplayClass_FoldsAndMergesIntoOriginMethod()
    {
        // Real coverlet shape for `async () => …` inside RunAsync: the lambda's state machine
        // nests INSIDE the display class — Ns.Type/<>c/<<RunAsync>b__0_0>d + MoveNext.
        // Regression: first-'>' bracket parsing dropped this entry entirely (it appeared in
        // neither Methods nor Unscored), turning the gate falsely green.
        var origin = Method("Probe.Lib", "RunAsync", "()", complexity: 1, lines: [(5, 1)]);
        var lambda = Method("Probe.Lib/<>c/<<RunAsync>b__0_0>d", "MoveNext", "()", complexity: 6,
            lines: [(10, 0), (11, 0), (12, 0)]);

        var report = CrapAnalysis.Analyze([origin, lambda]);

        var m = await Assert.That(report.Methods).HasSingleItem();
        await Assert.That(m.Method).IsEqualTo("Probe.Lib.RunAsync");
        await Assert.That(m.Complexity).IsEqualTo(6);          // Math.Max of origin and folded state machine
        await Assert.That(m.Coverage).IsEqualTo(0.25);         // line 5 hit; 10–12 missed
    }

    [Test]
    public async Task AsyncLambdaStateMachine_WithoutOriginEntry_StandsAsItsOwnRow_AndFailsGate()
    {
        var lambda = Method("MyApp.Svc/<>c/<<Run>b__2_0>d", "MoveNext", "()", complexity: 9,
            lines: [(20, 0), (21, 0), (22, 0)]);

        var report = CrapAnalysis.Analyze([lambda]);

        var m = await Assert.That(report.Methods).HasSingleItem();
        await Assert.That(m.Method).IsEqualTo("MyApp.Svc.Run");
        await Assert.That(m.Score).IsEqualTo(90.0);            // 9²·1³ + 9
        await Assert.That(report.Evaluate(6).Outcome).IsEqualTo(GateOutcome.Fail);
    }

    [Test]
    public async Task AsyncLambdaStateMachine_InsideCapturingDisplayClass_FoldsToOriginMethod()
    {
        // A capturing async lambda nests its state machine inside <>c__DisplayClass…, not <>c.
        var sm = Method("MyApp.C/<>c__DisplayClass3_0/<<M>b__0>d", "MoveNext", "()",
            complexity: 2, lines: [(7, 1)]);

        await Assert.That(CrapAnalysis.Analyze([sm]).Methods.Single().Method).IsEqualTo("MyApp.C.M");
    }

    [Test]
    public async Task AsyncLambdaStateMachine_Infrastructure_IsDropped()
    {
        var report = CrapAnalysis.Analyze([
            Method("MyApp.Svc/<>c/<<Run>b__2_0>d", ".ctor", "()", complexity: 1, lines: [(1, 1)]),
            Method("MyApp.Svc/<>c/<<Run>b__2_0>d", "SetStateMachine",
                "(System.Runtime.CompilerServices.IAsyncStateMachine)", complexity: 1, lines: [(1, 1)]),
        ]);

        await Assert.That(report.Methods).IsEmpty();
        await Assert.That(report.Unscored).IsEmpty();
    }

    [Test]
    public async Task TopLevelStatements_AsyncEntryPoint_NormalizesToProgramMain()
    {
        // Program/<<Main>$>d__0 + MoveNext: the '$' suffix and the nested brackets both parse.
        var sm = Method("Program/<<Main>$>d__0", "MoveNext", "()", complexity: 3,
            lines: [(1, 1), (2, 0)]);

        var m = await Assert.That(CrapAnalysis.Analyze([sm]).Methods).HasSingleItem();
        await Assert.That(m.Method).IsEqualTo("Program.Main");
        await Assert.That(m.Complexity).IsEqualTo(3);
    }

    [Test]
    public async Task TopLevelStatements_SyncEntryPoint_NormalizesToProgramMain()
    {
        var mc = Method("Program", "<Main>$", "(System.String[])", complexity: 1, lines: [(1, 1)]);

        await Assert.That(CrapAnalysis.Analyze([mc]).Methods.Single().Method).IsEqualTo("Program.Main");
    }

    [Test]
    public async Task AsyncLocalFunction_StateMachine_FoldsToContainingMethod()
    {
        var sm = Method("MyApp.C/<<M>g__Local|0_0>d", "MoveNext", "()", complexity: 2, lines: [(3, 1)]);

        await Assert.That(CrapAnalysis.Analyze([sm]).Methods.Single().Method).IsEqualTo("MyApp.C.M");
    }

    // ── Same-arity overloads stay distinct ────────────────────────────────────

    [Test]
    public async Task SameArityOverloads_StayDistinct_UncoveredOverloadStillFailsGate()
    {
        // Regression: keying logical methods by ARITY merged Frob(Int32) into Frob(String),
        // diluting the uncovered comp-20 overload's CRAP 420 to a passing 23.2.
        var risky = Method("MyApp.Svc", "Frob", "(System.Int32)", complexity: 20,
            lines: [(10, 0), (11, 0), (12, 0), (13, 0), (14, 0)]);
        var covered = Method("MyApp.Svc", "Frob", "(System.String)", complexity: 1,
            lines: [.. Enumerable.Range(30, 20).Select(l => (l, 1))]);

        var report = CrapAnalysis.Analyze([risky, covered]);

        await Assert.That(report.Methods.Count).IsEqualTo(2);
        await Assert.That(report.Methods).Contains(m => m is { Complexity: 20, Score: 420.0 });
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.Fail);
    }

    [Test]
    public async Task FoldedStateMachine_AmbiguousAmongSameArityOverloads_StandsAsItsOwnRow()
    {
        // Two same-arity Run overloads: the folded <Run>d__2 group has no unambiguous origin,
        // so it must not merge into either — it stands as its own row (and can fail the gate).
        var a = Method("MyApp.Calc", "Run", "(System.Int32)", complexity: 1, lines: [(10, 1)]);
        var b = Method("MyApp.Calc", "Run", "(System.String)", complexity: 1, lines: [(20, 1)]);
        var sm = Method("MyApp.Calc/<Run>d__2", "MoveNext", "()", complexity: 5,
            lines: [(30, 0), (31, 0)]);

        var report = CrapAnalysis.Analyze([a, b, sm]);

        await Assert.That(report.Methods.Count).IsEqualTo(3);
        await Assert.That(report.Methods).Contains(m => m is { Complexity: 5, Coverage: 0.0, Score: 30.0 });
        await Assert.That(report.Evaluate(6).Outcome).IsEqualTo(GateOutcome.Fail);
    }

    // ── Complexity resolution ─────────────────────────────────────────────────

    [Test]
    public async Task EmbeddedComplexity_WinsOverMetricsFile()
    {
        var mc = Method("MyApp.A", "M", "(System.Int32)", complexity: 2, lines: [(1, 1)]);
        var metrics = new[] { Member("MyApp.A", "M", CodeMetricsMemberKind.Method, arity: 1, complexity: 9) };

        var m = await Assert.That(CrapAnalysis.Analyze([mc], metrics).Methods).HasSingleItem();

        await Assert.That(m.Complexity).IsEqualTo(2);
        await Assert.That(m.ComplexitySource).IsEqualTo(CrapComplexitySource.CoverageReport);
    }

    [Test]
    public async Task MetricsFile_FillsInWhenCoverageHasNoComplexity()
    {
        var mc = Method("MyApp.A", "M", "(System.Int32)", complexity: null, lines: [(1, 0)]);
        var metrics = new[] { Member("MyApp.A", "M", CodeMetricsMemberKind.Method, arity: 1, complexity: 5) };

        var report = CrapAnalysis.Analyze([mc], metrics);

        var m = await Assert.That(report.Methods).HasSingleItem();
        await Assert.That(m.Complexity).IsEqualTo(5);
        await Assert.That(m.ComplexitySource).IsEqualTo(CrapComplexitySource.MetricsFile);
        await Assert.That(m.Score).IsEqualTo(30.0);   // comp 5, cov 0
        await Assert.That(report.UnmatchedMetricsMembers).IsEmpty();
    }

    [Test]
    public async Task UnparenthesizedSignature_ArityUnknown_StillMatchesByName()
    {
        // Some emitters omit or truncate the signature attribute: arity is then "unknown",
        // never zero, and the name match still resolves against the metrics table.
        var mc = Method("MyApp.A", "M", "", complexity: null, lines: [(1, 1)]);
        var metrics = new[] { Member("MyApp.A", "M", CodeMetricsMemberKind.Method, arity: 2, complexity: 4) };

        var m = await Assert.That(CrapAnalysis.Analyze([mc], metrics).Methods).HasSingleItem();

        await Assert.That(m.Complexity).IsEqualTo(4);
        await Assert.That(m.ComplexitySource).IsEqualTo(CrapComplexitySource.MetricsFile);
    }

    [Test]
    public async Task UnbalancedMangledName_NeverMatchesAsSynthetic_StaysItsOwnRow()
    {
        // A method name that LOOKS mangled but never closes its bracket (`<Broken`) must not be
        // folded anywhere — the bracket parse requires the matching close, so the entry stands
        // under its literal name instead of corrupting some other method's coverage.
        var mc = Method("MyApp.A", "<Broken", "()", complexity: 1, lines: [(1, 1)]);

        var m = await Assert.That(CrapAnalysis.Analyze([mc]).Methods).HasSingleItem();

        await Assert.That(m.Method).IsEqualTo("MyApp.A.<Broken");
    }

    [Test]
    public async Task Overloads_DisambiguatedByArity()
    {
        var oneArg = Method("MyApp.A", "M", "(System.Int32)", complexity: null, lines: [(1, 1)]);
        var twoArg = Method("MyApp.A", "M", "(System.Int32,System.Int32)", complexity: null, lines: [(5, 1)]);
        var metrics = new[]
        {
            Member("MyApp.A", "M", CodeMetricsMemberKind.Method, arity: 1, complexity: 2),
            Member("MyApp.A", "M", CodeMetricsMemberKind.Method, arity: 2, complexity: 7),
        };

        var report = CrapAnalysis.Analyze([oneArg, twoArg], metrics);

        await Assert.That(report.Methods.Count).IsEqualTo(2);
        await Assert.That(report.Methods).Contains(m => m.Complexity == 2);
        await Assert.That(report.Methods).Contains(m => m.Complexity == 7);
        await Assert.That(report.UnmatchedMetricsMembers).IsEmpty();
    }

    [Test]
    public async Task Accessor_GetPrefix_MatchesMetricsAccessorEntry()
    {
        var mc = Method("MyApp.A", "get_Value", "()", complexity: null, lines: [(3, 1)]);
        var metrics = new[] { Member("MyApp.A", "get_Value", CodeMetricsMemberKind.Accessor, arity: null, complexity: 1) };

        var m = await Assert.That(CrapAnalysis.Analyze([mc], metrics).Methods).HasSingleItem();

        await Assert.That(m.Complexity).IsEqualTo(1);
    }

    [Test]
    public async Task Accessor_FallsBackToPropertyAggregate_WhenNoAccessorEntries()
    {
        // Older Metrics versions emit no <Accessors>: the property aggregate is the coarser
        // honest fallback.
        var mc = Method("MyApp.A", "get_Value", "()", complexity: null, lines: [(3, 1)]);
        var metrics = new[] { Member("MyApp.A", "Value", CodeMetricsMemberKind.Property, arity: null, complexity: 2) };

        var m = await Assert.That(CrapAnalysis.Analyze([mc], metrics).Methods).HasSingleItem();

        await Assert.That(m.Complexity).IsEqualTo(2);
    }

    // ── Honesty channels ──────────────────────────────────────────────────────

    [Test]
    public async Task MissingMetricsFile_MethodWithoutComplexity_ListedAsUnscored()
    {
        var scored = Method("MyApp.A", "WithComp", "()", complexity: 2, lines: [(1, 1)]);
        var unscored = Method("MyApp.A", "NoComp", "()", complexity: null, lines: [(5, 1)]);

        var report = CrapAnalysis.Analyze([scored, unscored]);

        await Assert.That(report.Methods).HasSingleItem();
        var u = await Assert.That(report.Unscored).HasSingleItem();
        await Assert.That(u.Method).IsEqualTo("MyApp.A.NoComp");
        await Assert.That(u.Reason).Contains("--metrics");
        // The unscored method never fails the gate — but is never silently dropped either.
        await Assert.That(report.Evaluate(6).Outcome).IsEqualTo(GateOutcome.Pass);
    }

    [Test]
    public async Task MetricsMemberMatchingNothing_ListedAsUnmatched()
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

        await Assert.That(report.UnmatchedMetricsMembers).IsEquivalentTo(["void A.Ghost()"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task MethodWithNoLines_ListedAsUnscored()
    {
        var report = CrapAnalysis.Analyze([Method("MyApp.A", "Empty", "()", complexity: 3, lines: [])]);

        await Assert.That(report.Methods).IsEmpty();
        await Assert.That(report.Unscored.Single().Reason).Contains("no line data");
    }

    // ── Exclusions ────────────────────────────────────────────────────────────

    [Test]
    public async Task ExcludeFiles_SameSemanticsAsReportExclude()
    {
        var methods = new[]
        {
            Method("MyApp.A", "M", "()", complexity: 1, lines: [(1, 1)], file: "src/A.cs"),
            Method("MyApp.Migrations.X", "Up", "()", complexity: 1, lines: [(1, 1)], file: "src/Migrations/X.cs"),
            Method("MyApp.Program", "Main", "()", complexity: 1, lines: [(1, 1)], file: "Program.cs"),
        };

        var filtered = CrapAnalysis.ExcludeFiles(methods, ExclusionRules.WellKnown, keep: ["Program.cs"]);

        await Assert.That(filtered.Count).IsEqualTo(2);
        await Assert.That(filtered).DoesNotContain(m => m.File.Contains("Migrations"));
        await Assert.That(filtered).Contains(m => m.File == "Program.cs");   // keep wins, incl. the virtual-root anchor
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