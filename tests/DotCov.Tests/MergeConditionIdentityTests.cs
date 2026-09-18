using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

/// <summary>
/// The condition-identity mismatch arm of <see cref="FileCoverage.MergeWith"/> and the
/// presentation-time condition overlay. Two properties are pinned: (1) a keyset mismatch
/// permanently POISONS the line's condition identity (empty-dict sentinel, absorbing), so the
/// merged aggregate no longer depends on the order reports are folded in; (2) the
/// condition-derived aggregate is overlaid onto the presented line aggregate in FromLineData
/// — never baked into merge state, where a Math.Max could not be undone by a later poison.
/// </summary>
public sealed class MergeConditionIdentityTests
{
    // Three reports of the same branched line 5 ("25% (1/4)" each). A and B agree on the
    // condition-number set {1,2}; C carries {1,3} — a different build's IL offsets. The only
    // order-independent honest union is the raw line-level aggregate (1/4): C's mismatch
    // poisons per-condition identity for the line no matter when C arrives.
    private static CoverageReport A() => Cobertura.NewDoc()
        .AddClass("src/Foo.cs", c => c.BranchWithConditions(5, "25% (1/4)", (1, "0%"), (2, "50%")))
        .Parse();

    private static CoverageReport B() => Cobertura.NewDoc()
        .AddClass("src/Foo.cs", c => c.BranchWithConditions(5, "25% (1/4)", (1, "50%"), (2, "0%")))
        .Parse();

    private static CoverageReport C() => Cobertura.NewDoc()
        .AddClass("src/Foo.cs", c => c.BranchWithConditions(5, "25% (1/4)", (1, "0%"), (3, "50%")))
        .Parse();

    [Test]
    [Arguments("ABC")]
    [Arguments("ACB")]
    [Arguments("BAC")]
    [Arguments("BCA")]
    [Arguments("CAB")]
    [Arguments("CBA")]
    public async Task Merge_MismatchedIdentityReport_EveryFoldOrderConvergesToTheRawAggregate(string order)
    {
        // Pre-fix, directory order A,B,C yielded 2/4 (A∪B overlaid before C's mismatch) while
        // C,A,B yielded 1/4 — the same uploads passed or failed `check --min-branch 50`
        // depending on TestResults subdirectory naming.
        var merged = order
            .Select(name => name switch { 'A' => A(), 'B' => B(), _ => C() })
            .Aggregate(CoverageReport.Merge);

        var f = merged.Files[0];
        await Assert.That(f.BranchesHit).IsEqualTo(1);
        await Assert.That(f.BranchesTotal).IsEqualTo(4);
        await Assert.That(f.ConditionsByLine[5]).IsEmpty();   // poisoned sentinel survives the whole fold
        await Assert.That(merged.Warnings).Contains(static w =>
            w.Kind is CoverageWarningKind.ConditionIdentityMismatch && w.Line == 5);
    }

    [Test]
    public async Task Merge_EqualCountPartiallyOverlappingConditionSets_WarnsInsteadOfThrowing()
    {
        // {0,1} vs {1,2}: equal counts, overlapping but different sets. The identity gate must
        // reject this via the full keyset check — a mutant that accepts it (All -> Any) indexes
        // theirs[0] and throws KeyNotFoundException.
        var a = Cobertura.NewDoc()
            .AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "50% (2/4)", (0, "100%"), (1, "0%")))
            .Parse();
        var b = Cobertura.NewDoc()
            .AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "50% (2/4)", (1, "0%"), (2, "100%")))
            .Parse();

        var merged = CoverageReport.Merge(a, b);
        var f = merged.Files[0];

        await Assert.That(f.BranchesHit).IsEqualTo(2);        // line-level aggregate kept
        await Assert.That(f.BranchesTotal).IsEqualTo(4);
        await Assert.That(f.ConditionsByLine[10]).IsEmpty();  // poisoned
        await Assert.That(merged.Warnings).Contains(static w =>
            w.Kind is CoverageWarningKind.ConditionIdentityMismatch && w.Line == 10);
    }

    [Test]
    public async Task Merge_PoisonedLine_AbsorbsLaterDetailWithoutReWarning()
    {
        // Once poisoned (A+C mismatch, which warns), a later report's detail must neither
        // resurrect per-condition union nor emit a second mismatch warning for the sentinel.
        var poisoned = CoverageReport.Merge(A(), C());
        await Assert.That(poisoned.Warnings).HasSingleItem();

        var merged = CoverageReport.Merge(poisoned, B());

        await Assert.That(merged.Warnings).HasSingleItem();        // only the carried-forward original
        await Assert.That(merged.Files[0].ConditionsByLine[5]).IsEmpty();
        await Assert.That(merged.Files[0].BranchesHit).IsEqualTo(1);
    }

    // ── The one-sided overlay (C4): condition detail proving more than the line aggregate ──

    /// <summary>
    /// Two class blocks with complementary conditions: per-condition Math.Max across blocks
    /// yields {0:2, 1:2} (derived 4/4) while the line-level aggregate stays (2/4). Passes
    /// Materialize's consistency gate (2 conditions * 2 == total 4).
    /// </summary>
    private static CoverageReport DetailProvesFullCoverage() => Cobertura.NewDoc()
        .AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "50% (2/4)", (0, "100%"), (1, "0%")))
        .AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "50% (2/4)", (0, "0%"), (1, "100%")))
        .Parse();

    [Test]
    public async Task Parse_ConditionUnionAcrossClassBlocks_RaisesThePresentedAggregate()
    {
        // The overlay lives in FromLineData, so the derived 4/4 is already visible at parse
        // time — the same split-condition union logic Merge applies across reports.
        var f = DetailProvesFullCoverage().Files[0];

        await Assert.That(f.BranchesByLine[10]).IsEqualTo((4, 4));
        await Assert.That(f.BranchesHit).IsEqualTo(4);
    }

    [Test]
    public async Task Merge_OneSidedDetail_OverlaysTheAggregateInBothMergeOrders()
    {
        // Merged with a detail-less report whose line-level value is (2/4), the carried
        // condition detail must still prove (4/4) — in BOTH orders. Deleting either one-sided
        // overlay arm leaves (2/4) in one order and makes the merge order-dependent.
        var b = Cobertura.NewDoc()
            .AddClass("src/Foo.cs", c => c.Branch(10, "50% (2/4)"))
            .Parse();

        var ab = CoverageReport.Merge(DetailProvesFullCoverage(), b).Files[0];
        var ba = CoverageReport.Merge(b, DetailProvesFullCoverage()).Files[0];

        await Assert.That(ab.BranchesByLine[10]).IsEqualTo((4, 4));
        await Assert.That(ba.BranchesByLine[10]).IsEqualTo((4, 4));
        await Assert.That(ab.BranchesHit).IsEqualTo(4);
        await Assert.That(ba.BranchesHit).IsEqualTo(ab.BranchesHit);
        await Assert.That(ba.BranchesTotal).IsEqualTo(ab.BranchesTotal);
    }

    [Test]
    public async Task Merge_OneSidedDetail_NeverShrinksAMismatchKeptTotal()
    {
        // a: line (1/2) with condition {0:1} (derived total 2). b: line (1/4), no detail.
        // The BranchTotalMismatch warning says "keeping 4" — the overlay's Total component
        // must be Math.Max, so the condition-derived total 2 cannot regress the kept 4.
        var a = Cobertura.NewDoc()
            .AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "50% (1/2)", (0, "50%")))
            .Parse();
        var b = Cobertura.NewDoc()
            .AddClass("src/Foo.cs", c => c.Branch(10, "25% (1/4)"))
            .Parse();

        var merged = CoverageReport.Merge(a, b);
        var f = merged.Files[0];

        await Assert.That(f.BranchesByLine[10]).IsEqualTo((1, 4));
        await Assert.That(f.BranchesTotal).IsEqualTo(4);
        await Assert.That(merged.Warnings).Contains(static w =>
            w.Kind is CoverageWarningKind.BranchTotalMismatch && w.Line == 10 && w.Detail.Contains("keeping 4"));
    }

    [Test]
    public async Task Merge_ConditionIdentityMismatch_DetailNamesBothNumberSetsAscending()
    {
        // The warning must expose both divergent condition-number sets in ascending order.
        var a = Cobertura.NewDoc()
            .AddClass("x.cs", c => c.BranchWithConditions(5, "50% (2/4)", (1, "50%"), (3, "50%")))
            .Parse();
        var b = Cobertura.NewDoc()
            .AddClass("x.cs", c => c.BranchWithConditions(5, "50% (2/4)", (2, "100%"), (4, "0%")))
            .Parse();

        var merged = CoverageReport.Merge(a, b);

        var w = await Assert.That(merged.Warnings).HasSingleItem();
        await Assert.That(w.Kind).IsEqualTo(CoverageWarningKind.ConditionIdentityMismatch);
        await Assert.That(w.File).IsEqualTo("x.cs");
        await Assert.That(w.Line).IsEqualTo(5);
        await Assert.That(w.Detail).IsEqualTo("condition numbers [1,3] vs [2,4] - using the line aggregate");
    }
}
