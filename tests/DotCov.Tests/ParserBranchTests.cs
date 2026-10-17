using System.Text;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;
[TestClass]
public sealed class ParserBranchTests(TestContext testContext)
{
    [TestMethod]
    public void Parse_RepeatedBranchLineWithDifferingValues_ReconcilesPerComponentMax()
    {
        // Vary both covered and total counts so each component's maximum is checked independently.
        var f = Cobertura.NewDoc().AddClass("x.cs", c => c.Branch(5, "(1/2)")).AddClass("x.cs", c => c.Branch(5, "(2/4)")).Parse().Files[0];
        Assert.AreEqual((2, 4), f.BranchesByLine[5]);
        Assert.AreEqual(2, f.BranchesHit);
        Assert.AreEqual(4, f.BranchesTotal);
    }

    [TestMethod]
    public void Parse_FullyCoveredBranchLine_IsAbsentFromPartialBranches()
    {
        var f = Cobertura.NewDoc().AddClass("src/A.cs", c => c.Branch(5, "100% (2/2)").Branch(10, "50% (1/2)")).Parse().Files[0];
        var partial = Assert.ContainsSingle(f.PartialBranches);
        Assert.AreEqual(10, partial.Line);
        Assert.AreEqual(1, partial.Covered);
        Assert.AreEqual(2, partial.Total);
    }

    [TestMethod]
    public void Parse_OutOfOrderLinesAcrossClassBlocks_SortsUncoveredAndPartialBranchOutput()
    {
        // Nested types can emit earlier lines later; output ordering must not depend on insertion order.
        var f = Cobertura.NewDoc().AddClass("a.cs", c => c.Line(10, hits: 0).Branch(12, "50% (1/2)")).AddClass("a.cs", c => c.Line(5, hits: 0).Branch(6, "50% (1/2)")).Parse().Files[0];
        Assert.AreSequenceEqual([5, 10], f.UncoveredLines, SequenceOrder.InOrder);
        Assert.AreSequenceEqual([6, 12], f.PartialBranches.Select(static b => b.Line), SequenceOrder.InOrder);
    }

    [TestMethod]
    public async Task Parse_ValidConditionBeforeAnyLine_IsIgnoredWithoutCrashing()
    {
        // A condition without a preceding line has no line to attribute coverage to.
        const string xml = """
                           <?xml version="1.0"?>
                           <coverage><packages><package><classes>
                             <class name="X" filename="x.cs">
                               <lines>
                                 <condition number="7" coverage="100%" />
                                 <line number="1" hits="1" branch="false" />
                               </lines>
                             </class>
                           </classes></package></packages></coverage>
                           """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var report = await CoberturaParser.ParseAsync(stream, ct: testContext.CancellationToken);
        var f = Assert.ContainsSingle(report.Files);
        Assert.IsEmpty(f.ConditionsByLine);
        Assert.AreEqual(1, f.LinesHit);
    }

    [TestMethod]
    public async Task Parse_ConditionWithoutCoverageAttribute_RecordsNoPhantomDetail()
    {
        const string xml = """
                           <?xml version="1.0"?>
                           <coverage><packages><package><classes>
                             <class name="X" filename="x.cs">
                               <lines>
                                 <line number="10" hits="1" branch="true" condition-coverage="50% (1/2)">
                                   <conditions>
                                     <condition number="0" />
                                   </conditions>
                                 </line>
                               </lines>
                             </class>
                           </classes></package></packages></coverage>
                           """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var f = Assert.ContainsSingle((await CoberturaParser.ParseAsync(stream, ct: testContext.CancellationToken)).Files);
        Assert.IsEmpty(f.ConditionsByLine); // aggregate-only fallback
        Assert.AreEqual(1, f.BranchesHit); // line aggregate (1/2) preserved
        Assert.AreEqual(2, f.BranchesTotal);
    }

    [TestMethod]
    public void Parse_BranchOnLineZero_StillCollectsConditionDetail()
    {
        var f = Cobertura.NewDoc().AddClass("z.cs", c => c.BranchWithConditions(0, "50% (1/2)", (0, "50%"))).Parse().Files[0];
        Assert.AreEqual((1, 2), f.BranchesByLine[0]);
        Assert.ContainsSingle(f.ConditionsByLine);
        var conds = Assert.ContainsSingle(f.ConditionsByLine).Value;
        Assert.AreEqual(1, conds[0]);
    }
}
