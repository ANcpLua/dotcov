using System.Text;
using DotCov.Tests.Infrastructure;
using TUnit.Assertions.Enums;

namespace DotCov.Tests;

public sealed class ParserBranchTests
{
    [Test]
    public async Task Parse_RepeatedBranchLineWithDifferingValues_ReconcilesPerComponentMax()
    {
        // Vary both covered and total counts so each component's maximum is checked independently.
        var f = Cobertura.NewDoc()
            .AddClass("x.cs", c => c.Branch(5, "(1/2)"))
            .AddClass("x.cs", c => c.Branch(5, "(2/4)"))
            .Parse().Files[0];

        await Assert.That(f.BranchesByLine[5]).IsEqualTo((2, 4));
        await Assert.That(f.BranchesHit).IsEqualTo(2);
        await Assert.That(f.BranchesTotal).IsEqualTo(4);
    }

    [Test]
    public async Task Parse_FullyCoveredBranchLine_IsAbsentFromPartialBranches()
    {
        // Fully covered branches must not appear in the user-facing partial-branch list.
        var f = Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c
                .Branch(5, "100% (2/2)")
                .Branch(10, "50% (1/2)"))
            .Parse().Files[0];

        var partial = await Assert.That(f.PartialBranches).HasSingleItem();
        await Assert.That(partial.Line).IsEqualTo(10);
        await Assert.That(partial.Covered).IsEqualTo(1);
        await Assert.That(partial.Total).IsEqualTo(2);
    }

    [Test]
    public async Task Parse_OutOfOrderLinesAcrossClassBlocks_SortsUncoveredAndPartialBranchOutput()
    {
        // Nested types can emit earlier lines later; output ordering must not depend on insertion order.
        var f = Cobertura.NewDoc()
            .AddClass("a.cs", c => c.Line(10, hits: 0).Branch(12, "50% (1/2)"))
            .AddClass("a.cs", c => c.Line(5, hits: 0).Branch(6, "50% (1/2)"))
            .Parse().Files[0];

        await Assert.That(f.UncoveredLines).IsEquivalentTo([5, 10], CollectionOrdering.Matching);
        await Assert.That(f.PartialBranches.Select(static b => b.Line)).IsEquivalentTo([6, 12], CollectionOrdering.Matching);
    }

    [Test]
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
        var report = await CoberturaParser.ParseAsync(stream);

        var f = await Assert.That(report.Files).HasSingleItem();
        await Assert.That(f.ConditionsByLine).IsEmpty();
        await Assert.That(f.LinesHit).IsEqualTo(1);
    }

    [Test]
    public async Task Parse_ConditionWithoutCoverageAttribute_RecordsNoPhantomDetail()
    {
        // Missing condition coverage must retain the line aggregate without inventing detail.
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
        var f = await Assert.That((await CoberturaParser.ParseAsync(stream)).Files).HasSingleItem();

        await Assert.That(f.ConditionsByLine).IsEmpty();      // aggregate-only fallback
        await Assert.That(f.BranchesHit).IsEqualTo(1);        // line aggregate (1/2) preserved
        await Assert.That(f.BranchesTotal).IsEqualTo(2);
    }

    [Test]
    public async Task Parse_BranchOnLineZero_StillCollectsConditionDetail()
    {
        // Line zero is a valid condition attribution target.
        var f = Cobertura.NewDoc()
            .AddClass("z.cs", c => c.BranchWithConditions(0, "50% (1/2)", (0, "50%")))
            .Parse().Files[0];

        await Assert.That(f.BranchesByLine[0]).IsEqualTo((1, 2));
        await Assert.That(f.ConditionsByLine).HasSingleItem();
        var conds = f.ConditionsByLine.Single().Value;
        await Assert.That(conds[0]).IsEqualTo(1);
    }
}
