using TUnit.Assertions.Enums;
using System.Text;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

/// <summary>
/// Pins for documented-but-untested parser/report behavior found by mutation analysis:
/// each test here kills at least one mutant that survived the full pre-existing suite.
/// </summary>
public sealed class CoreMutationPinTests
{
    [Test]
    public async Task Parse_RepeatedBranchLineWithDifferingValues_ReconcilesPerComponentMax()
    {
        // The header contract: the same branched line re-emitted across <class> blocks
        // reconciles via Math.Max on BOTH tuple components. Varying covered AND total in one
        // line ((1/2) then (2/4)) pins each component independently — with equal totals a
        // Min mutant on the Total position is indistinguishable from Max.
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
        // FromLineData's classification is `Covered < Total` — strictly less. A 2/2 line in
        // PartialBranches would corrupt the JSON partialBranches array and the "needs tests"
        // guidance for every fully-exercised branch in the report.
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
        // Every fixture happens to emit ascending line numbers, so dictionary insertion order
        // coincidentally equals sorted order and the ordering guarantees were deletable. A
        // later <class> block covering EARLIER lines (state machines, nested types) produces
        // out-of-order insertion for real — the user-visible lists must still come out sorted.
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
        // A well-formed <condition> with valid number/coverage attributes arriving before the
        // first <line> in a class subtree must be silently ignored (there is no line to
        // attribute it to). The -1 sentinel is what protects this: a corrupted initial value
        // would record the condition against a line with no branch aggregate and crash
        // Materialize's direct index.
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
        var report = CoberturaParser.Parse(stream);

        var f = await Assert.That(report.Files).HasSingleItem();
        await Assert.That(f.ConditionsByLine).IsEmpty();
        await Assert.That(f.LinesHit).IsEqualTo(1);
    }

    [Test]
    public async Task Parse_ConditionWithoutCoverageAttribute_RecordsNoPhantomDetail()
    {
        // A coverage-less <condition> on a (1/2) line would — if the null guard were lost —
        // record a phantom {0:0} that passes Materialize's count*2==total gate straight into
        // the public ConditionsByLine, where a merge union can Math.Max it against real data.
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
        var f = await Assert.That(CoberturaParser.Parse(stream).Files).HasSingleItem();

        await Assert.That(f.ConditionsByLine).IsEmpty();      // aggregate-only fallback
        await Assert.That(f.BranchesHit).IsEqualTo(1);        // line aggregate (1/2) preserved
        await Assert.That(f.BranchesTotal).IsEqualTo(2);
    }
}