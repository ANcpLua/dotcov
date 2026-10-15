using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;
/// <summary>
/// The condition-identity mismatch arm of <see cref = "FileCoverage.MergeWith"/> and the
/// presentation-time condition overlay. Two properties are pinned: (1) a keyset mismatch
/// permanently POISONS the line's condition identity (empty-dict sentinel, absorbing), so the
/// merged aggregate does not depend on the order reports are folded in; (2) the
/// condition-derived aggregate is overlaid onto the presented line aggregate in FromLineData
/// — never baked into merge state, where a Math.Max could not be undone by a later poison.
/// </summary>
[TestClass]
public sealed class MergeConditionIdentityTests
{
    // Three reports of the same branched line 5 ("25% (1/4)" each). A and B agree on the
    // condition-number set {1,2}; C carries {1,3} — a different build's IL offsets. The only
    // order-independent honest union is the raw line-level aggregate (1/4): C's mismatch
    // poisons per-condition identity for the line no matter when C arrives.
    private static CoverageReport A() => Cobertura.NewDoc().AddClass("src/Foo.cs", c => c.BranchWithConditions(5, "25% (1/4)", (1, "0%"), (2, "50%"))).Parse();
    private static CoverageReport B() => Cobertura.NewDoc().AddClass("src/Foo.cs", c => c.BranchWithConditions(5, "25% (1/4)", (1, "50%"), (2, "0%"))).Parse();
    private static CoverageReport C() => Cobertura.NewDoc().AddClass("src/Foo.cs", c => c.BranchWithConditions(5, "25% (1/4)", (1, "0%"), (3, "50%"))).Parse();
    [TestMethod]
    [DataRow("ABC")]
    [DataRow("ACB")]
    [DataRow("BAC")]
    [DataRow("BCA")]
    [DataRow("CAB")]
    [DataRow("CBA")]
    public void Merge_MismatchedIdentityReport_EveryFoldOrderConvergesToTheRawAggregate(string order)
    {
        // An order-dependent merge would yield 2/4 for A,B,C (A∪B overlaid before C's mismatch)
        // but 1/4 for C,A,B — the same uploads would pass or fail `check --min-branch 50`
        // depending on TestResults subdirectory naming.
        var merged = order.Select(name => name switch
        {
            'A' => A(),
            'B' => B(),
            _ => C()}).Aggregate(CoverageReport.Merge);
        var f = merged.Files[0];
        Assert.AreEqual(1, f.BranchesHit);
        Assert.AreEqual(4, f.BranchesTotal);
        Assert.IsEmpty(f.ConditionsByLine[5]); // poisoned sentinel survives the whole fold
        Assert.Contains(static w => w.Kind is CoverageWarningKind.ConditionIdentityMismatch && w.Line == 5, merged.Warnings);
    }

    [TestMethod]
    public void Merge_EqualCountPartiallyOverlappingConditionSets_WarnsInsteadOfThrowing()
    {
        // {0,1} vs {1,2}: equal counts, overlapping but different sets. The identity gate must
        // reject this via the full keyset check — a mutant that accepts it (All -> Any) indexes
        // theirs[0] and throws KeyNotFoundException.
        var a = Cobertura.NewDoc().AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "50% (2/4)", (0, "100%"), (1, "0%"))).Parse();
        var b = Cobertura.NewDoc().AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "50% (2/4)", (1, "0%"), (2, "100%"))).Parse();
        var merged = CoverageReport.Merge(a, b);
        var f = merged.Files[0];
        Assert.AreEqual(2, f.BranchesHit); // line-level aggregate kept
        Assert.AreEqual(4, f.BranchesTotal);
        Assert.IsEmpty(f.ConditionsByLine[10]); // poisoned
        Assert.Contains(static w => w.Kind is CoverageWarningKind.ConditionIdentityMismatch && w.Line == 10, merged.Warnings);
    }

    [TestMethod]
    public void Merge_PoisonedLine_AbsorbsLaterDetailWithoutReWarning()
    {
        // Once poisoned (A+C mismatch, which warns), a later report's detail must neither
        // resurrect per-condition union nor emit a second mismatch warning for the sentinel.
        var poisoned = CoverageReport.Merge(A(), C());
        Assert.ContainsSingle(poisoned.Warnings);
        var merged = CoverageReport.Merge(poisoned, B());
        Assert.ContainsSingle(merged.Warnings); // only the carried-forward original
        Assert.IsEmpty(merged.Files[0].ConditionsByLine[5]);
        Assert.AreEqual(1, merged.Files[0].BranchesHit);
    }

    // ── The one-sided overlay: condition detail proving more than the line aggregate ──
    /// <summary>
    /// Two class blocks with complementary conditions: per-condition Math.Max across blocks
    /// yields {0:2, 1:2} (derived 4/4) while the line-level aggregate stays (2/4). Passes
    /// Materialize's consistency gate (2 conditions * 2 == total 4).
    /// </summary>
    private static CoverageReport DetailProvesFullCoverage() => Cobertura.NewDoc().AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "50% (2/4)", (0, "100%"), (1, "0%"))).AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "50% (2/4)", (0, "0%"), (1, "100%"))).Parse();
    [TestMethod]
    public void Parse_ConditionUnionAcrossClassBlocks_RaisesThePresentedAggregate()
    {
        // The overlay lives in FromLineData, so the derived 4/4 is already visible at parse
        // time — the same split-condition union logic Merge applies across reports.
        var f = DetailProvesFullCoverage().Files[0];
        Assert.AreEqual((4, 4), f.BranchesByLine[10]);
        Assert.AreEqual(4, f.BranchesHit);
    }

    [TestMethod]
    public void Merge_OneSidedDetail_OverlaysTheAggregateInBothMergeOrders()
    {
        // Merged with a detail-less report whose line-level value is (2/4), the carried
        // condition detail must still prove (4/4) — in BOTH orders. Deleting either one-sided
        // overlay arm leaves (2/4) in one order and makes the merge order-dependent.
        var b = Cobertura.NewDoc().AddClass("src/Foo.cs", c => c.Branch(10, "50% (2/4)")).Parse();
        var ab = CoverageReport.Merge(DetailProvesFullCoverage(), b).Files[0];
        var ba = CoverageReport.Merge(b, DetailProvesFullCoverage()).Files[0];
        Assert.AreEqual((4, 4), ab.BranchesByLine[10]);
        Assert.AreEqual((4, 4), ba.BranchesByLine[10]);
        Assert.AreEqual(4, ab.BranchesHit);
        Assert.AreEqual(ab.BranchesHit, ba.BranchesHit);
        Assert.AreEqual(ab.BranchesTotal, ba.BranchesTotal);
    }

    [TestMethod]
    public void Merge_OneSidedDetail_NeverShrinksAMismatchKeptTotal()
    {
        // a: line (1/2) with condition {0:1} (derived total 2). b: line (1/4), no detail.
        // The BranchTotalMismatch warning says "keeping 4" — the overlay's Total component
        // must be Math.Max, so the condition-derived total 2 cannot regress the kept 4.
        var a = Cobertura.NewDoc().AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "50% (1/2)", (0, "50%"))).Parse();
        var b = Cobertura.NewDoc().AddClass("src/Foo.cs", c => c.Branch(10, "25% (1/4)")).Parse();
        var merged = CoverageReport.Merge(a, b);
        var f = merged.Files[0];
        Assert.AreEqual((1, 4), f.BranchesByLine[10]);
        Assert.AreEqual(4, f.BranchesTotal);
        Assert.Contains(static w => w.Kind is CoverageWarningKind.BranchTotalMismatch && w.Line == 10 && w.Detail.Contains("keeping 4"), merged.Warnings);
    }

    [TestMethod]
    public void Merge_ConditionIdentityMismatch_DetailNamesBothNumberSetsAscending()
    {
        var a = Cobertura.NewDoc().AddClass("x.cs", c => c.BranchWithConditions(5, "50% (2/4)", (1, "50%"), (3, "50%"))).Parse();
        var b = Cobertura.NewDoc().AddClass("x.cs", c => c.BranchWithConditions(5, "50% (2/4)", (2, "100%"), (4, "0%"))).Parse();
        var merged = CoverageReport.Merge(a, b);
        var w = Assert.ContainsSingle(merged.Warnings);
        Assert.AreEqual(CoverageWarningKind.ConditionIdentityMismatch, w.Kind);
        Assert.AreEqual("x.cs", w.File);
        Assert.AreEqual(5, w.Line);
        Assert.AreEqual("condition numbers [1,3] vs [2,4] - using the line aggregate", w.Detail);
    }
}
