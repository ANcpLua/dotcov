using System.Text;
using DotCov.Tests.Infrastructure;
using Xunit;

namespace DotCov.Tests;

/// <summary>
/// Pins for documented-but-untested parser/report behavior found by mutation analysis:
/// each test here kills at least one mutant that survived the full pre-existing suite.
/// </summary>
public sealed class CoreMutationPinTests
{
    [Fact]
    public void Parse_RepeatedBranchLineWithDifferingValues_ReconcilesPerComponentMax()
    {
        // The header contract: the same branched line re-emitted across <class> blocks
        // reconciles via Math.Max on BOTH tuple components. Varying covered AND total in one
        // line ((1/2) then (2/4)) pins each component independently — with equal totals a
        // Min mutant on the Total position is indistinguishable from Max.
        var f = Cobertura.NewDoc()
            .AddClass("x.cs", c => c.Branch(5, "(1/2)"))
            .AddClass("x.cs", c => c.Branch(5, "(2/4)"))
            .Parse().Files[0];

        Assert.Equal((2, 4), f.BranchesByLine[5]);
        Assert.Equal(2, f.BranchesHit);
        Assert.Equal(4, f.BranchesTotal);
    }

    [Fact]
    public void Parse_FullyCoveredBranchLine_IsAbsentFromPartialBranches()
    {
        // FromLineData's classification is `Covered < Total` — strictly less. A 2/2 line in
        // PartialBranches would corrupt the JSON partialBranches array and the "needs tests"
        // guidance for every fully-exercised branch in the report.
        var f = Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c
                .Branch(5, "100% (2/2)")
                .Branch(10, "50% (1/2)"))
            .Parse().Files[0];

        var partial = Assert.Single(f.PartialBranches);
        Assert.Equal(10, partial.Line);
        Assert.Equal(1, partial.Covered);
        Assert.Equal(2, partial.Total);
    }

    [Fact]
    public void Parse_OutOfOrderLinesAcrossClassBlocks_SortsUncoveredAndPartialBranchOutput()
    {
        // Every fixture happens to emit ascending line numbers, so dictionary insertion order
        // coincidentally equals sorted order and the ordering guarantees were deletable. A
        // later <class> block covering EARLIER lines (state machines, nested types) produces
        // out-of-order insertion for real — the user-visible lists must still come out sorted.
        var f = Cobertura.NewDoc()
            .AddClass("a.cs", c => c.Line(10, hits: 0).Branch(12, "50% (1/2)"))
            .AddClass("a.cs", c => c.Line(5, hits: 0).Branch(6, "50% (1/2)"))
            .Parse().Files[0];

        Assert.Equal([5, 10], f.UncoveredLines);
        Assert.Equal([6, 12], f.PartialBranches.Select(static b => b.Line));
    }

    [Fact]
    public void Parse_ValidConditionBeforeAnyLine_IsIgnoredWithoutCrashing()
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

        var f = Assert.Single(report.Files);
        Assert.Empty(f.ConditionsByLine);
        Assert.Equal(1, f.LinesHit);
    }

    [Fact]
    public void Parse_ConditionWithoutCoverageAttribute_RecordsNoPhantomDetail()
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
        var f = Assert.Single(CoberturaParser.Parse(stream).Files);

        Assert.Empty(f.ConditionsByLine);      // aggregate-only fallback
        Assert.Equal(1, f.BranchesHit);        // line aggregate (1/2) preserved
        Assert.Equal(2, f.BranchesTotal);
    }
}
