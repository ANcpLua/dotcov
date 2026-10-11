using System.Collections.ObjectModel;
using DotCov.Tests.Infrastructure;
using DotCov.Tool;

namespace DotCov.Tests;

/// <summary>
/// <see cref="CrapAnalysis"/> against the IL names Roslyn really emits (verified by compiling
/// and reflecting over the shapes below) and the metrics displays it really writes: what folds
/// into which source method, what stays distinct, which metrics member each method matches —
/// and the gate boundaries that must never report a pass nobody measured.
/// </summary>
public sealed class CrapAnalysisEdgeTests
{
    // ── Distinct methods stay distinct ────────────────────────────────────────

    [Test]
    public async Task GenericAndNonGenericTypeOfOneName_SameMethod_StayDistinctRows()
    {
        // Result and Result<T> are different types (IL: Result, Result`1). Merging their
        // same-signature methods unioned an uncovered complex method into a covered one —
        // across two files, by line number.
        var plain = Method("MyApp.Result", "Map", "(System.Int32)", complexity: 1,
            lines: [(10, 1), (11, 1)], file: "src/Result.cs");
        var generic = Method("MyApp.Result`1", "Map", "(System.Int32)", complexity: 8,
            lines: [(10, 0), (11, 0), (12, 0)], file: "src/ResultOfT.cs");

        var report = CrapAnalysis.Analyze([plain, generic]);

        await Assert.That(report.Methods.Count).IsEqualTo(2);
        await Assert.That(report.Methods).Contains(m => m is { File: "src/Result.cs", Complexity: 1, Coverage: 1.0 });
        await Assert.That(report.Methods).Contains(m => m is { File: "src/ResultOfT.cs", Complexity: 8, Coverage: 0.0, Score: 72.0 });
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.Fail);
    }

    [Test]
    public async Task LambdaInGenericType_FoldsIntoTheGenericTypesMethod_NotAmbiguousWithTheNonGenericOne()
    {
        var plain = Method("MyApp.Result", "Map", "()", complexity: 1, lines: [(10, 1)], file: "src/Result.cs");
        var generic = Method("MyApp.Result`1", "Map", "()", complexity: 1, lines: [(20, 1)], file: "src/ResultOfT.cs");
        var lambda = Method("MyApp.Result`1/<>c", "<Map>b__3_0", "(System.Int32)", complexity: 4,
            lines: [(21, 0)], file: "src/ResultOfT.cs");

        var report = CrapAnalysis.Analyze([plain, generic, lambda]);

        await Assert.That(report.Methods.Count).IsEqualTo(2);
        await Assert.That(report.Methods).Contains(m => m is { File: "src/ResultOfT.cs", Complexity: 4, Coverage: 0.5 });
        await Assert.That(report.Methods).Contains(m => m is { File: "src/Result.cs", Complexity: 1, Coverage: 1.0 });
    }

    // ── Folding into the origin method ────────────────────────────────────────

    [Test]
    public async Task AsyncLambdaInConstructor_StateMachineFoldsIntoTheConstructor()
    {
        // Roslyn replaces '.' with '-' in synthesized TYPE names, so an async lambda in a
        // constructor (or a field initializer) gets the state machine <>c/<<-ctor>b__1_0>d.
        var ctor = Method("MyApp.Svc", ".ctor", "()", complexity: 1, lines: [(5, 1)]);
        var sm = Method("MyApp.Svc/<>c/<<-ctor>b__1_0>d", "MoveNext", "()", complexity: 6,
            lines: [(6, 0), (7, 0), (8, 0)]);

        var report = CrapAnalysis.Analyze([ctor, sm]);

        var m = await Assert.That(report.Methods).HasSingleItem();
        await Assert.That(m.Method).IsEqualTo("MyApp.Svc..ctor");
        await Assert.That(m.Complexity).IsEqualTo(6);
        await Assert.That(m.Coverage).IsEqualTo(0.25);
    }

    [Test]
    public async Task ExplicitInterfaceIterator_StateMachineFoldsIntoTheImplementation()
    {
        // `IEnumerator IEnumerable.GetEnumerator() { yield ... }`: the method is
        // System.Collections.IEnumerable.GetEnumerator, its state machine
        // <System-Collections-IEnumerable-GetEnumerator>d__3.
        var stub = Method("MyApp.Bag", "System.Collections.IEnumerable.GetEnumerator", "()", complexity: 1,
            lines: [(12, 1)]);
        var sm = Method("MyApp.Bag/<System-Collections-IEnumerable-GetEnumerator>d__3", "MoveNext", "()",
            complexity: 3, lines: [(13, 0)]);

        var report = CrapAnalysis.Analyze([stub, sm]);

        var m = await Assert.That(report.Methods).HasSingleItem();
        await Assert.That(m.Method).IsEqualTo("MyApp.Bag.System.Collections.IEnumerable.GetEnumerator");
        await Assert.That(m.Complexity).IsEqualTo(3);
        await Assert.That(m.Coverage).IsEqualTo(0.5);
    }

    [Test]
    public async Task LambdaOnTheOriginsOwnLine_MergesPerLineWithMax()
    {
        // `var ys = xs.Select(x => x * 2);` — the lambda's only line is the origin's line.
        var origin = Method("MyApp.C", "Run", "()", complexity: 1, lines: [(5, 1), (6, 0)]);
        var lambda = Method("MyApp.C/<>c", "<Run>b__0_0", "(System.Int32)", complexity: 1, lines: [(6, 4)]);

        var m = await Assert.That(CrapAnalysis.Analyze([origin, lambda]).Methods).HasSingleItem();

        await Assert.That(m.Coverage).IsEqualTo(1.0);   // 2 distinct lines, both hit — not 2 of 3
    }

    // ── Metrics matching with real Roslyn displays ────────────────────────────

    [Test]
    public async Task ExplicitInterfaceImplementation_MatchesItsMetricsMember()
    {
        // MinimallyQualifiedFormat drops the interface: `void Bag.Dispose()`. IL keeps it:
        // System.IDisposable.Dispose.
        var metrics = CodeMetricsReportBuilder.ParseType("MyApp", "Bag", m => m
            .Method("void Bag.Dispose()", 4)
            .Property("int Bag.Current", 2, a => a.Method("int Bag.Current.get", 2)));
        var dispose = Method("MyApp.Bag", "System.IDisposable.Dispose", "()", complexity: null, lines: [(3, 0)]);
        var current = Method("MyApp.Bag", "System.Collections.Generic.IEnumerator<System.Int32>.get_Current", "()",
            complexity: null, lines: [(7, 1)]);

        var report = CrapAnalysis.Analyze([dispose, current], metrics);

        await Assert.That(report.Unscored).IsEmpty();
        await Assert.That(report.UnmatchedMetricsMembers).IsEmpty();
        await Assert.That(report.Methods).Contains(m => m is { Method: "MyApp.Bag.System.IDisposable.Dispose", Complexity: 4, Score: 20.0 });
        await Assert.That(report.Methods).Contains(m => m.Method.EndsWith(".get_Current", StringComparison.Ordinal) && m.Complexity == 2);
    }

    [Test]
    public async Task StaticConstructor_MatchesItsMetricsMember()
    {
        // A static constructor displays exactly like a parameterless instance one:
        // `Calc.Calc()`. IL names it .cctor.
        var metrics = CodeMetricsReportBuilder.ParseType("MyApp", "Calc", m => m
            .Method("Calc.Calc()", 5)
            .Method("Calc.Calc(int seed)", 1));
        var cctor = Method("MyApp.Calc", ".cctor", "()", complexity: null, lines: [(2, 0)]);
        var ctor = Method("MyApp.Calc", ".ctor", "(System.Int32)", complexity: null, lines: [(4, 1)]);

        var report = CrapAnalysis.Analyze([cctor, ctor], metrics);

        await Assert.That(report.Unscored).IsEmpty();
        await Assert.That(report.UnmatchedMetricsMembers).IsEmpty();
        await Assert.That(report.Methods).Contains(m => m is { Method: "MyApp.Calc..cctor", Complexity: 5, Score: 30.0 });
        await Assert.That(report.Methods).Contains(m => m is { Method: "MyApp.Calc..ctor", Complexity: 1 });
    }

    [Test]
    public async Task ExtensionBlockMembers_MatchTheStaticClassImplementations()
    {
        // IL: P.Ext.Twice(string) and P.Ext.get_IsBig(string) — the receiver becomes the first
        // parameter, so arity differs from the display and the name match decides.
        var metrics = CodeMetricsReportBuilder.ParseType("P", "Ext.extension(string)", m => m
            .Method("int extension(string).Twice()", 3)
            .Property("bool extension(string).IsBig", 2, a => a.Method("bool extension(string).IsBig.get", 2)));
        var twice = Method("P.Ext", "Twice", "(System.String)", complexity: null, lines: [(6, 0)]);
        var isBig = Method("P.Ext", "get_IsBig", "(System.String)", complexity: null, lines: [(7, 1)]);

        var report = CrapAnalysis.Analyze([twice, isBig], metrics);

        await Assert.That(report.Unscored).IsEmpty();
        await Assert.That(report.UnmatchedMetricsMembers).IsEmpty();
        await Assert.That(report.Methods).Contains(m => m is { Method: "P.Ext.Twice", Complexity: 3, Score: 12.0 });
        await Assert.That(report.Methods).Contains(m => m is { Method: "P.Ext.get_IsBig", Complexity: 2 });
    }

    [Test]
    public async Task PositionalRecordProperty_ZeroAggregate_IsNoComplexityMeasurement()
    {
        // `record R(int X)`: Roslyn writes <Property Name="int R.X"> with complexity 0 and no
        // Accessors (it declares none), yet R.get_X has IL and a sequence point. The aggregate
        // sums zero accessors; using it scored get_X with comp 0 and CRAP 0.0 — a value the
        // formula cannot produce for real code (comp ≥ 1, as MethodCoverage states).
        var metrics = CodeMetricsReportBuilder.ParseType("MyApp", "R", m => m
            .Method("R.R(int X)", 1)
            .Property("int R.X", 0));
        var getX = Method("MyApp.R", "get_X", "()", complexity: null, lines: [(3, 0)]);

        var report = CrapAnalysis.Analyze([getX], metrics);

        await Assert.That(report.Methods).IsEmpty();
        var u = await Assert.That(report.Unscored).HasSingleItem();
        await Assert.That(u.Method).IsEqualTo("MyApp.R.get_X");
    }

    [Test]
    public async Task TopLevelProgram_RealMetricsShape_ScoresAndFailsTheGate()
    {
        // The real shape for top-level statements: <Namespace Name="<global namespace>">,
        // <NamedType Name="Program">, <Method Name="<top-level-statements-entry-point>">.
        // It used to match nothing, so the riskiest method went unscored while a trivial
        // namespaced one passed the gate.
        using var ws = TempWorkspace.Create("dotcov-crap-toplevel-");
        var metrics = ws.Write("app.Metrics.xml", CodeMetricsReportBuilder.Create()
            .Namespace("<global namespace>", t => t.Type("Program", m => m.Method("<top-level-statements-entry-point>", 8)))
            .Namespace("MyApp", t => t.Type("Util", m => m.Method("int Util.Helper()", 1)))
            .ToBytes());
        var coverage = ws.Write("coverage.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/Program.cs", "Program", c => c
                .Method("<Main>$", "(System.String[])", null, m => m.Line(1, hits: 0).Line(2, hits: 0)))
            .AddClass("src/Program.cs", "Program/<<Main>$>d__0", c => c
                .Method("MoveNext", "()", null, m => m.Line(3, hits: 0)))
            .AddClass("src/Util.cs", "MyApp.Util", c => c
                .Method("Helper", "()", null, m => m.Line(1, hits: 1))));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = await DotCovCli.RunAsync(["crap", coverage, "--metrics", metrics], stdout, stderr);

        await Assert.That(code).IsEqualTo(1);
        await Assert.That(stderr.ToString()).StartsWith("FAIL:");
        await Assert.That(stdout.ToString()).Contains("Program.Main");
        await Assert.That(stdout.ToString()).DoesNotContain("Unmatched");
        await Assert.That(stdout.ToString()).DoesNotContain("Unscored");
    }

    [Test]
    public async Task MetricsSupplied_ButNoCounterpart_ListedUnscoredWithTheMetricsReason()
    {
        var metrics = CodeMetricsReportBuilder.ParseType("MyApp", "Calc", m => m.Method("int Calc.Add(int a, int b)", 2));
        var orphan = Method("MyApp.Calc", "Sub", "(System.Int32,System.Int32)", complexity: null, lines: [(9, 0)]);

        var report = CrapAnalysis.Analyze([orphan], metrics);

        await Assert.That(report.Methods).IsEmpty();
        var u = await Assert.That(report.Unscored).HasSingleItem();
        await Assert.That(u.Method).IsEqualTo("MyApp.Calc.Sub");
        await Assert.That(u.Reason).IsEqualTo("no matching member in the metrics file");
        await Assert.That(report.UnmatchedMetricsMembers).IsEquivalentTo(["int Calc.Add(int a, int b)"]);
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.NoData);
    }

    [Test]
    public async Task UnparseableSignature_ArityUnknown_TakesTheMaxAcrossOverloads()
    {
        var metrics = new[]
        {
            Member("MyApp.A", "M", arity: 1, complexity: 2),
            Member("MyApp.A", "M", arity: 2, complexity: 7),
        };

        foreach (var signature in new[] { "(System.Int32", "System.Int32)", "x" })
        {
            var report = CrapAnalysis.Analyze([Method("MyApp.A", "M", signature, complexity: null, lines: [(1, 1)])], metrics);

            var m = await Assert.That(report.Methods).HasSingleItem();
            await Assert.That(m.Complexity).IsEqualTo(7);
            await Assert.That(report.UnmatchedMetricsMembers).IsEmpty();
        }
    }

    // ── Compiler infrastructure and unusual names ─────────────────────────────

    [Test]
    public async Task AnonymousTypeMembers_HaveNoSourceMethod_AndAreDropped()
    {
        // Anonymous types are emitted at the top level: <>f__AnonymousType0`2.
        var report = CrapAnalysis.Analyze([
            Method("<>f__AnonymousType0`2", "get_Name", "()", complexity: 1, lines: [(4, 1)]),
            Method("<>f__AnonymousType0`2", "ToString", "()", complexity: 2, lines: [(4, 0)]),
        ]);

        await Assert.That(report.Methods).IsEmpty();
        await Assert.That(report.Unscored).IsEmpty();
    }

    [Test]
    public async Task DisplayClassConstructor_IsInfrastructure_AndDropped()
    {
        var report = CrapAnalysis.Analyze([
            Method("MyApp.C/<>c__DisplayClass0_0", ".ctor", "()", complexity: 1, lines: [(3, 1)]),
            Method("MyApp.C/<>c", ".cctor", "()", complexity: 1, lines: [(3, 1)]),
        ]);

        await Assert.That(report.Methods).IsEmpty();
        await Assert.That(report.Unscored).IsEmpty();
    }

    [Test]
    public async Task PlainSegmentInsideSyntheticContainer_IsMachinery_NotATypeName()
    {
        var lambda = Method("MyApp.C/<>c__DisplayClass0_0/Inner", "<Run>b__1", "()", complexity: 3, lines: [(9, 1)]);
        var ctor = Method("MyApp.C/<>c__DisplayClass0_0/Inner", ".ctor", "()", complexity: 1, lines: [(9, 1)]);

        var report = CrapAnalysis.Analyze([lambda, ctor]);

        var m = await Assert.That(report.Methods).HasSingleItem();
        await Assert.That(m.Method).IsEqualTo("MyApp.C.Run");
        await Assert.That(m.Complexity).IsEqualTo(3);
    }

    [Test]
    public async Task BracketedSegmentThatIsNoStateMachine_StaysATypeSegment()
    {
        // <PrivateImplementationDetails> and fixed-buffer types (<buf>e__FixedBuffer) are
        // bracketed but neither state machines nor display classes.
        var report = CrapAnalysis.Analyze([
            Method("<PrivateImplementationDetails>", "ComputeStringHash", "(System.String)", complexity: 3, lines: [(1, 1)]),
            Method("MyApp.S/<buf>e__FixedBuffer", "Get", "()", complexity: 1, lines: [(2, 1)]),
        ]);

        await Assert.That(report.Methods.Select(m => m.Method)).IsEquivalentTo(
            ["<PrivateImplementationDetails>.ComputeStringHash", "MyApp.S.<buf>e__FixedBuffer.Get"]);
    }

    [Test]
    public async Task BracketedMethodNameThatIsNoLambda_IsNotFolded()
    {
        var report = CrapAnalysis.Analyze([
            Method("MyApp.C", "Run", "()", complexity: 1, lines: [(1, 1)]),
            Method("MyApp.C", "<Run>x__0", "()", complexity: 5, lines: [(2, 0)]),
        ]);

        await Assert.That(report.Methods.Count).IsEqualTo(2);
        await Assert.That(report.Methods).Contains(m => m is { Method: "MyApp.C.<Run>x__0", Complexity: 5 });
    }

    // ── Gate boundaries ───────────────────────────────────────────────────────

    [Test]
    public async Task FailingVerdict_NeverPrintsAWorstScoreThatReadsAsAtThreshold()
    {
        // comp 29, 25 of 28 lines covered: CRAP = 29 + 841·(3/28)³ = 30.034 > 30 → FAIL. Plain
        // F1 printed "FAIL: worst CRAP 30.0 (max 30)" — at threshold, which passes. Same rule as
        // GateResult: the figure that decided the verdict rounds toward it.
        var lines = Enumerable.Range(1, 28).Select(l => (l, l <= 25 ? 1 : 0)).ToArray();
        var report = CrapAnalysis.Analyze([Method("MyApp.A", "Big", "()", complexity: 29, lines: lines)]);

        var gate = report.Evaluate(30);

        await Assert.That(gate.Outcome).IsEqualTo(GateOutcome.Fail);
        await Assert.That(gate.WorstScore!.Value).IsEqualTo(30.034).Within(0.001);
        await Assert.That(gate.ToString()).IsEqualTo("FAIL: worst CRAP 30.1 (max 30) - 1 of 1 methods above threshold");
    }

    [Test]
    public async Task PassingVerdict_NeverPrintsAWorstScoreThatReadsAsAboveThreshold()
    {
        // Same score against max 30.04: at or below → PASS, and the summary must not read 30.1.
        var lines = Enumerable.Range(1, 28).Select(l => (l, l <= 25 ? 1 : 0)).ToArray();
        var report = CrapAnalysis.Analyze([Method("MyApp.A", "Big", "()", complexity: 29, lines: lines)]);

        var gate = report.Evaluate(30.04);

        await Assert.That(gate.Outcome).IsEqualTo(GateOutcome.Pass);
        await Assert.That(gate.ToString()).IsEqualTo("PASS: worst CRAP 30.0 (max 30.04) - all 1 methods at or below threshold");
    }

    [Test]
    public async Task ExactFailingScore_PrintsUnchanged_DespiteFloatNoise()
    {
        // CRAP 42 exactly (comp 6, uncovered) must print 42.0, not round up to 42.1.
        var report = CrapAnalysis.Analyze([Method("MyApp.A", "M", "()", complexity: 6, lines: [(1, 0)])]);

        await Assert.That(report.Evaluate(30).ToString()).StartsWith("FAIL: worst CRAP 42.0 (max 30)");
    }

    [Test]
    public async Task ToString_FailJustAboveMax_NeverPrintsTheMax()
    {
        // Routed from hunter 4: comp 6, 12 of 95 lines → CRAP 30.0086 > 30. The formatter's
        // offender row prints 30.1 (ceiling); the closing verdict must agree with it.
        var hits = Enumerable.Range(1, 95).ToDictionary(l => l, l => l <= 12 ? 1 : 0);
        var report = CrapAnalysis.Analyze([new MethodCoverage("MyApp.A", "Risky", "()", "src/A.cs", 1, 95, 12, 95, 6)
            { LineHits = new ReadOnlyDictionary<int, int>(hits) }]);

        var gate = report.Evaluate(30);   // CRAP 30.0086 → Fail

        await Assert.That(gate.Outcome).IsEqualTo(GateOutcome.Fail);
        await Assert.That(gate.ToString()).IsEqualTo("FAIL: worst CRAP 30.1 (max 30) - 1 of 1 methods above threshold");
    }

    [Test]
    public async Task ToString_FailAtAnExactTenth_WithFloatNoiseAbove_IsNotRaisedATenth()
    {
        // comp 9, cov 1/3: CRAP is exactly 33 but computes to 33.00000000000001. A bare
        // ceiling would print 33.1; the epsilon keeps it at the true 33.0.
        var report = CrapAnalysis.Analyze([Method("MyApp.A", "M", "()", complexity: 9, lines: [(1, 1), (2, 0), (3, 0)])]);

        var gate = report.Evaluate(30);

        await Assert.That(gate.WorstScore!.Value).IsGreaterThan(33.0);   // the noise is real
        await Assert.That(gate.ToString()).IsEqualTo("FAIL: worst CRAP 33.0 (max 30) - 1 of 1 methods above threshold");
    }

    [Test]
    public async Task Evaluate_NaNThreshold_IsNeverAPass()
    {
        // No score is "at or below" NaN. The line/branch gate fails closed on NaN; this one
        // reported PASS: "all 1 methods at or below threshold".
        var report = CrapAnalysis.Analyze([Method("MyApp.A", "M", "()", complexity: 2, lines: [(1, 1)])]);

        var gate = report.Evaluate(double.NaN);

        await Assert.That(gate.IsPass).IsFalse();
        await Assert.That(gate.Outcome).IsEqualTo(GateOutcome.Fail);
        await Assert.That(gate.AboveThreshold).IsEqualTo(1);
    }

    [Test]
    public async Task Evaluate_InfiniteThreshold_NoMethods_IsDisabledWithoutWorstScore()
    {
        var gate = CrapAnalysis.Analyze([]).Evaluate(double.PositiveInfinity);

        await Assert.That(gate.Outcome).IsEqualTo(GateOutcome.Disabled);
        await Assert.That(gate.WorstScore).IsNull();
        await Assert.That(gate.ScoredMethods).IsEqualTo(0);
        await Assert.That(gate.ToString()).IsEqualTo("DISABLED: worst CRAP n/a (max Infinity) - no finite threshold - this gate cannot fail");
    }

    [Test]
    public async Task Evaluate_InfiniteThreshold_WithMethods_ReportsWorstScoreButIsDisabled()
    {
        var report = CrapAnalysis.Analyze([Method("MyApp.A", "M", "()", complexity: 3, lines: [(1, 0)])]);

        var gate = report.Evaluate(double.PositiveInfinity);

        await Assert.That(gate.Outcome).IsEqualTo(GateOutcome.Disabled);
        await Assert.That(gate.WorstScore).IsEqualTo(12.0);
        await Assert.That(gate.IsPass).IsFalse();
    }

    [Test]
    public async Task ExcludeFiles_NoPatterns_ReturnsTheInputUnchanged()
    {
        IReadOnlyList<MethodCoverage> methods =
        [
            Method("MyApp.Migrations.X", "Up", "()", complexity: 1, lines: [(1, 1)], file: "src/Migrations/X.cs"),
        ];

        var filtered = CrapAnalysis.ExcludeFiles(methods, [], keep: ["src/Migrations/X.cs"]);

        await Assert.That(filtered).IsSameReferenceAs(methods);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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

    private static CodeMetricsMember Member(string typeName, string memberName, int? arity, int complexity) =>
        new(typeName, memberName, CodeMetricsMemberKind.Method, arity, complexity, $"{typeName}.{memberName}");
}
