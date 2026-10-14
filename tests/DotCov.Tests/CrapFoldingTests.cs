using System.Collections.ObjectModel;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

/// <summary>
/// Which coverage entries become one CRAP row. A row averages the lines it folds, so folding two
/// methods that are not one dilutes the riskier one's score: entries fold only within one file,
/// overloads keep their own rows, and compiler-synthesized material attaches to its origin only
/// when the origin is proven.
/// </summary>
public sealed class CrapFoldingTests
{
    // ── A2: one row per file ──────────────────────────────────────────────────

    [Test]
    public async Task SameMethodInTwoFiles_IsTwoRows_NotOneAveragedRow()
    {
        // Two assemblies each define Ns.T.M(Int32): one never runs, the other always does.
        var methods = Cobertura.NewDoc()
            .AddClass("svc-a/T.cs", "Ns.T", c => c.Method("M", "(System.Int32)", "3", m => m.Line(1, 0).Line(2, 0)))
            .AddClass("svc-b/T.cs", "Ns.T", c => c.Method("M", "(System.Int32)", "3", m => m.Line(1, 1).Line(2, 1)))
            .ParseMethods();

        var report = CrapAnalysis.Analyze(methods);

        await Assert.That(report.Methods.Select(static m => (m.Method, m.File, m.Coverage, m.Score))).IsEquivalentTo(
        [
            ("Ns.T.M", "svc-a/T.cs", 0.0, 12.0),   // 3²·1³ + 3
            ("Ns.T.M", "svc-b/T.cs", 1.0, 3.0),
        ]);
        await Assert.That(report.Evaluate(6).Outcome).IsEqualTo(GateOutcome.Fail);
    }

    [Test]
    public async Task SyntheticMaterial_NeverAttachesToASameNamedMethodInAnotherFile()
    {
        // A partial class is one type, but a lambda's body lives in the file of its origin: a
        // same-named method in another file is not proven to be that origin.
        var report = CrapAnalysis.Analyze(
        [
            Method("Ns.T", "M", "()", 1, [(10, 1)], file: "src/T.Part1.cs"),
            Method("Ns.T/<>c", "<M>b__0_0", "()", 4, [(30, 0)], file: "src/T.Part2.cs"),
        ]);

        await Assert.That(report.Methods.Select(static m => (m.File, m.Complexity))).IsEquivalentTo(
            [("src/T.Part1.cs", 1), ("src/T.Part2.cs", 4)]);
    }

    // ── A3: overloads keep their own synthetic material ───────────────────────

    [Test]
    public async Task AsyncOverloads_KeepTheirOwnRowsAndScores()
    {
        // M(int) and M(string), both async: coverlet writes only their state machines. The
        // ordinals (d__0, d__1) keep them apart instead of averaging their lines into one row.
        var methods = Cobertura.NewDoc()
            .AddClass("src/T.cs", "Ns.T/<M>d__0", c => c.Method("MoveNext", "()", "2", m => m.Line(5, 1).Line(6, 1)))
            .AddClass("src/T.cs", "Ns.T/<M>d__1", c => c.Method("MoveNext", "()", "4", m => m.Line(12, 0).Line(13, 0)))
            .ParseMethods();

        var report = CrapAnalysis.Analyze(methods);

        await Assert.That(report.Methods.Select(static m => (m.Method, m.StartLine, m.Coverage, m.Score))).IsEquivalentTo(
        [
            ("Ns.T.M", 5, 1.0, 2.0),
            ("Ns.T.M", 12, 0.0, 20.0),   // 4²·1³ + 4: not diluted to a passing average
        ]);
        await Assert.That(report.Unscored).IsEmpty();
        await Assert.That(report.Evaluate(6).Outcome).IsEqualTo(GateOutcome.Fail);
    }

    [Test]
    public async Task AsyncOverloads_WithBothKickoffEntries_StayApart()
    {
        // The shape the folded key used to average: two named overloads AND both state
        // machines. No name proves which machine belongs to which overload, so nothing attaches.
        var report = CrapAnalysis.Analyze(
        [
            Method("Ns.T", "M", "(System.Int32)", 1, [(4, 1)]),
            Method("Ns.T", "M", "(System.String)", 1, [(10, 1)]),
            Method("Ns.T/<M>d__0", "MoveNext", "()", 2, [(5, 1)]),
            Method("Ns.T/<M>d__1", "MoveNext", "()", 4, [(11, 0)]),
        ]);

        await Assert.That(report.Methods.Count).IsEqualTo(4);
        await Assert.That(report.Methods).Contains(static m => m is { StartLine: 11, Score: 20.0 });
    }

    [Test]
    public async Task SyncOverloadAndAsyncOverload_TheStateMachineDoesNotJoinTheSyncBody()
    {
        // M(int) is synchronous and has lines of its own; <M>d__1 is the whole body of the async
        // M(string), whose kickoff coverlet never writes. A method's own state machine never
        // joins an entry with lines: that entry is a different overload.
        var report = CrapAnalysis.Analyze(
        [
            Method("Ns.T", "M", "(System.Int32)", 1, [(4, 1)]),
            Method("Ns.T/<M>d__1", "MoveNext", "()", 5, [(11, 0), (12, 0)]),
        ]);

        await Assert.That(report.Methods.Select(static m => (m.StartLine, m.Complexity, m.Score)))
            .IsEquivalentTo([(4, 1, 1.0), (11, 5, 30.0)]);
    }

    [Test]
    public async Task LambdasOfTwoOverloads_StayTwoGroups()
    {
        // <M>b__0_0 belongs to method #0, <M>b__1_0 to method #1: two origins. With two overloads
        // named M there is no telling which is #0, so both lambdas stand as their own rows —
        // never one row averaging both.
        var report = CrapAnalysis.Analyze(
        [
            Method("Ns.T", "M", "(System.Int32)", 1, [(4, 1)]),
            Method("Ns.T", "M", "(System.String)", 1, [(10, 1)]),
            Method("Ns.T/<>c", "<M>b__0_0", "(System.Int32)", 3, [(5, 1)]),
            Method("Ns.T/<>c", "<M>b__1_0", "(System.Int32)", 3, [(11, 0)]),
        ]);

        await Assert.That(report.Methods.Count).IsEqualTo(4);
        await Assert.That(report.Methods).Contains(static m => m is { StartLine: 5, Coverage: 1.0 });
        await Assert.That(report.Methods).Contains(static m => m is { StartLine: 11, Coverage: 0.0 });
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task AsyncOverloadWithLambda_KeepsItsOwnBody_RegardlessOfEntryOrder(bool lambdaFirst)
    {
        // The covered sync overload would dilute the uncovered async body from 42 to 10.5
        // if the first lambda's OwnBody=false survived merging the state machine.
        var sync = Method("Ns.T", "M", "(System.Int32)", 1, [(4, 1), (5, 1), (6, 1)]);
        var lambda = Method("Ns.T/<>c", "<M>b__1_0", "()", 1, [(12, 0)]);
        var body = Method("Ns.T/<M>d__1", "MoveNext", "()", 6, [(11, 0), (12, 0), (13, 0)]);

        var report = CrapAnalysis.Analyze(lambdaFirst ? [sync, lambda, body] : [sync, body, lambda]);

        await Assert.That(report.Methods.Select(static m => (m.StartLine, m.Complexity, m.Coverage, m.Score)))
            .IsEquivalentTo([(4, 1, 1.0, 1.0), (11, 6, 0.0, 42.0)]);
        await Assert.That(report.Unscored).IsEmpty();
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.Fail);
    }

    [Test]
    public async Task LambdasOfOneMethod_FoldIntoItsOnlyOverload()
    {
        // Everything synthesized for method #2 — two lambdas, a closure, a local function and
        // its state machine — is one origin, and one named M can only be that origin.
        var report = CrapAnalysis.Analyze(
        [
            Method("Ns.T", "M", "()", 1, [(4, 1)]),
            Method("Ns.T/<>c", "<M>b__2_0", "(System.Int32)", 2, [(5, 1)]),
            Method("Ns.T/<>c", "<M>b__2_1", "(System.Int32)", 2, [(6, 0)]),
            Method("Ns.T/<>c__DisplayClass2_0", "<M>b__0", "()", 3, [(7, 1)]),
            Method("Ns.T", "<M>g__Local|2_0", "()", 1, [(8, 1)]),
            Method("Ns.T/<<M>g__Run|2_1>d", "MoveNext", "()", 4, [(9, 1)]),
        ]);

        var m = await Assert.That(report.Methods).HasSingleItem();
        await Assert.That((m.Method, m.Complexity, m.Coverage)).IsEqualTo(("Ns.T.M", 4, 5.0 / 6));
    }

    /// <summary>
    /// Two synthetic entries for an origin named M and no named M: whether their names carry the
    /// same method ordinal decides one row or two. A bare lambda counter (<c>b__12</c>), a
    /// malformed suffix, or a missing ordinal is no ordinal at all, so such entries share one
    /// "unknown origin" row.
    /// </summary>
    [Test]
    [Arguments("Ns.T/<>c", "<M>b__0_1", "Ns.T/<>c", "<M>b__0_2", 1)]
    [Arguments("Ns.T/<>c", "<M>b__0_1", "Ns.T/<>c", "<M>b__1_0", 2)]
    [Arguments("Ns.T/<>c", "<M>b__12", "Ns.T/<>c", "<M>b__13", 1)]
    [Arguments("Ns.T/<>c", "<M>b__1x", "Ns.T/<>c", "<M>b__x_2", 1)]
    [Arguments("Ns.T", "<M>g__Local|2_0", "Ns.T", "<M>g__Other|3_0", 2)]
    [Arguments("Ns.T", "<M>g__Local", "Ns.T", "<M>g__Other|", 1)]
    [Arguments("Ns.T/<>c__DisplayClass3_0", "<M>b__0", "Ns.T/<>c__DisplayClass4_0", "<M>b__0", 2)]
    [Arguments("Ns.T/<>c__DisplayClass3_0", "<M>b__0", "Ns.T/<>c__DisplayClass3_1", "<M>b__1", 1)]
    [Arguments("Ns.T/<M>d__0", "MoveNext", "Ns.T/<M>d__1", "MoveNext", 2)]
    [Arguments("Ns.T/<M>d__", "MoveNext", "Ns.T/<M>d", "MoveNext", 1)]
    [Arguments("Ns.T/<>c/<<M>b__0_0>d", "MoveNext", "Ns.T/<>c/<<M>b__0_1>d", "MoveNext", 1)]
    [Arguments("Ns.T/<>c__DisplayClass3_0/<<M>b__0>d", "MoveNext", "Ns.T/<>c__DisplayClass4_0/<<M>b__0>d", "MoveNext", 2)]
    [Arguments("Ns.T/<<M>b__0_0>d__5", "MoveNext", "Ns.T/<<M>b__1_0>d__5", "MoveNext", 2)]
    [Arguments("Ns.T", "<<M>$>g__Local|4_0", "Ns.T/<>c", "<M>b__4_1", 1)]
    [Arguments("Ns.T/<>c__DisplayClass9_0", "<<M>g__Local|2_0>b__0", "Ns.T/<>c", "<M>b__2_5", 1)]
    public async Task SyntheticEntries_GroupByTheirOriginsMethodOrdinal(
        string classA, string methodA, string classB, string methodB, int rows)
    {
        var report = CrapAnalysis.Analyze(
        [
            Method(classA, methodA, "()", 1, [(5, 1)]),
            Method(classB, methodB, "()", 1, [(9, 0)]),
        ]);

        await Assert.That(report.Methods.Count).IsEqualTo(rows);
        await Assert.That(report.Methods).All(static m => m.Method == "Ns.T.M");
    }

    private static MethodCoverage Method(
        string className, string methodName, string signature, int complexity,
        (int Line, int Hits)[] lines, string file = "src/T.cs")
    {
        var hits = lines.ToDictionary(static l => l.Line, static l => l.Hits);
        return new MethodCoverage(
            className, methodName, signature, file,
            lines.Length is 0 ? 0 : lines.Min(static l => l.Line),
            lines.Length is 0 ? 0 : lines.Max(static l => l.Line),
            lines.Count(static l => l.Hits > 0), lines.Length, complexity)
        {
            LineHits = new ReadOnlyDictionary<int, int>(hits)
        };
    }
}
