using System.Text;
using System.Xml;
using Xunit;

namespace DotCov.Tests;

public sealed class CoberturaParserTests
{
    private const string FixturePath = "Fixtures/sample.cobertura.xml";

    [Fact]
    public void Parse_SampleFixture_ReturnsThreeFiles()
    {
        var report = CoberturaParser.ParseFile(FixturePath);
        Assert.Equal(3, report.Files.Count);
    }

    [Fact]
    public void Parse_FullyCoveredClass_ReportsAllLinesHit()
    {
        var report = CoberturaParser.ParseFile(FixturePath);
        var calculator = report.Files.Single(f => f.Path == "src/Calculator.cs");

        Assert.Equal(4, calculator.LinesTotal);
        Assert.Equal(4, calculator.LinesHit);
        Assert.Equal(1.0, calculator.LineRate);
    }

    [Fact]
    public void Parse_PartiallyCoveredClass_ReportsCorrectHitCount()
    {
        var report = CoberturaParser.ParseFile(FixturePath);
        var parser = report.Files.Single(f => f.Path == "src/Parser.cs");

        Assert.Equal(5, parser.LinesTotal);
        Assert.Equal(3, parser.LinesHit);
        Assert.Equal(0.6, parser.LineRate);
    }

    [Fact]
    public void Parse_UncoveredClass_ReportsZeroLineRate()
    {
        var report = CoberturaParser.ParseFile(FixturePath);
        var unused = report.Files.Single(f => f.Path == "src/Unused.cs");

        Assert.Equal(3, unused.LinesTotal);
        Assert.Equal(0, unused.LinesHit);
        Assert.Equal(0.0, unused.LineRate);
    }

    [Fact]
    public void Parse_FullBranchCoverage_ReportsAllBranchesHit()
    {
        var report = CoberturaParser.ParseFile(FixturePath);
        var calculator = report.Files.Single(f => f.Path == "src/Calculator.cs");

        Assert.Equal(2, calculator.BranchesTotal);
        Assert.Equal(2, calculator.BranchesHit);
        Assert.Equal(1.0, calculator.BranchRate);
    }

    [Fact]
    public void Parse_PartialBranches_ExtractsConditionCoverageCorrectly()
    {
        var report = CoberturaParser.ParseFile(FixturePath);
        var parser = report.Files.Single(f => f.Path == "src/Parser.cs");

        Assert.Equal(4, parser.BranchesTotal);
        Assert.Equal(1, parser.BranchesHit);
        Assert.Equal(0.25, parser.BranchRate);
    }

    [Fact]
    public void Parse_NoBranches_ReportsFullBranchRate()
    {
        var report = CoberturaParser.ParseFile(FixturePath);
        var unused = report.Files.Single(f => f.Path == "src/Unused.cs");

        Assert.Equal(0, unused.BranchesTotal);
        Assert.Equal(1.0, unused.BranchRate);
    }

    [Fact]
    public void Report_AggregateTotals_SumsAcrossAllFiles()
    {
        var report = CoberturaParser.ParseFile(FixturePath);

        Assert.Equal(12, report.TotalLines);
        Assert.Equal(7, report.TotalLinesHit);
        Assert.Equal(6, report.TotalBranches);
        Assert.Equal(3, report.TotalBranchesHit);
    }

    [Fact]
    public void MeetsThreshold_AboveMinimum_ReturnsTrue()
    {
        var report = CoberturaParser.ParseFile(FixturePath);
        Assert.True(report.MeetsThreshold(50));
    }

    [Fact]
    public void MeetsThreshold_BelowMinimum_ReturnsFalse()
    {
        var report = CoberturaParser.ParseFile(FixturePath);
        Assert.False(report.MeetsThreshold(80));
    }

    [Fact]
    public void MeetsThreshold_WithBranchMinimum_ChecksBoth()
    {
        var report = CoberturaParser.ParseFile(FixturePath);
        Assert.True(report.MeetsThreshold(50, 50));
        Assert.False(report.MeetsThreshold(50, 60));
    }

    [Fact]
    public void BelowPercent_ReturnsOnlyFilesUnderThreshold()
    {
        var report = CoberturaParser.ParseFile(FixturePath);
        var below80 = report.BelowPercent(80).ToList();

        Assert.Equal(2, below80.Count);
        Assert.Contains(below80, f => f.Path == "src/Parser.cs");
        Assert.Contains(below80, f => f.Path == "src/Unused.cs");
        Assert.DoesNotContain(below80, f => f.Path == "src/Calculator.cs");
    }

    [Fact]
    public void Parse_XmlWithDtd_Throws()
    {
        const string malicious = """
                                 <?xml version="1.0"?>
                                 <!DOCTYPE coverage [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
                                 <coverage><packages><package><classes>
                                   <class name="X" filename="x.cs"><lines>
                                     <line number="1" hits="1" branch="false"/>
                                   </lines></class>
                                 </classes></package></packages></coverage>
                                 """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(malicious));
        Assert.Throws<XmlException>(() => CoberturaParser.Parse(stream));
    }

    [Fact]
    public void Parse_EmptyPackages_ReturnsEmptyReport()
    {
        const string xml = """
                           <?xml version="1.0"?>
                           <coverage><packages></packages></coverage>
                           """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var report = CoberturaParser.Parse(stream);

        Assert.Empty(report.Files);
        Assert.Equal(1.0, report.LineRate);
    }

    // Regression: Coverlet emits each `<line>` twice — once under
    // `<class>/<methods>/<method>/<lines>` and once under `<class>/<lines>` (class-level
    // aggregate). The earlier parser used `ReadToDescendant("line")` + sibling iteration,
    // which landed inside the first method and silently dropped every subsequent method plus
    // the class-level block. Verified against real `dotnet test --collect:"XPlat Code
    // Coverage"` output: pre-fix branch count was ~24% of actual.
    [Fact]
    public void Parse_CoverletLayout_DedupesMethodsAndClassLines()
    {
        const string xml = """
                           <?xml version="1.0"?>
                           <coverage>
                             <packages>
                               <package>
                                 <classes>
                                   <class name="X" filename="x.cs">
                                     <methods>
                                       <method name="A" signature="()">
                                         <lines>
                                           <line number="10" hits="3" branch="False" />
                                           <line number="11" hits="3" branch="True" condition-coverage="50% (1/2)" />
                                         </lines>
                                       </method>
                                       <method name="B" signature="()">
                                         <lines>
                                           <line number="20" hits="0" branch="False" />
                                           <line number="21" hits="0" branch="True" condition-coverage="0% (0/2)" />
                                         </lines>
                                       </method>
                                     </methods>
                                     <lines>
                                       <line number="10" hits="3" branch="False" />
                                       <line number="11" hits="3" branch="True" condition-coverage="50% (1/2)" />
                                       <line number="20" hits="0" branch="False" />
                                       <line number="21" hits="0" branch="True" condition-coverage="0% (0/2)" />
                                     </lines>
                                   </class>
                                 </classes>
                               </package>
                             </packages>
                           </coverage>
                           """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var report = CoberturaParser.Parse(stream);
        var file = Assert.Single(report.Files);

        Assert.Equal(4, file.LinesTotal);
        Assert.Equal(2, file.LinesHit);
        Assert.Equal(4, file.BranchesTotal);
        Assert.Equal(1, file.BranchesHit);
    }

    // Regression: Coverlet writes `branch="True"` (PascalCase via XmlConvert.ToString(bool)),
    // original Cobertura/JaCoCo write `branch="true"`. Earlier code used a literal-pattern compare
    // and silently dropped every Coverlet branch line — surfaced as a fake 100% branch coverage.
    [Theory]
    [InlineData("50% (99999999999999/2)")]  // Groups[1] overflows int
    [InlineData("50% (1/99999999999999)")]  // Groups[2] overflows int
    public void Parse_ConditionCoverageWithIntOverflow_SkipsBranchSilently(string condition)
    {
        // Defensive: the regex `\(\d+/\d+\)` matches any-length digit sequences, so a
        // pathological emitter could write a number that overflows int. We skip the branch
        // entry instead of throwing — keeps the parser robust on hostile/old XML.
        var xml = $"""
                   <?xml version="1.0"?>
                   <coverage><packages><package><classes>
                     <class name="X" filename="x.cs">
                       <lines>
                         <line number="1" hits="1" branch="True" condition-coverage="{condition}" />
                       </lines>
                     </class>
                   </classes></package></packages></coverage>
                   """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var report = CoberturaParser.Parse(stream);

        Assert.Equal(0, report.Files[0].BranchesTotal);
    }

    [Fact]
    public void Parse_LineWithMissingHitsAttribute_TreatsAsZero()
    {
        // Guards the `int.TryParse(sub.GetAttribute("hits"), ...) ? h : 0` fallback —
        // the false leg fires when `hits` is missing or malformed.
        const string xml = """
                           <?xml version="1.0"?>
                           <coverage><packages><package><classes>
                             <class name="X" filename="x.cs">
                               <lines>
                                 <line number="1" branch="False" />
                                 <line number="2" hits="not-a-number" branch="False" />
                               </lines>
                             </class>
                           </classes></package></packages></coverage>
                           """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var report = CoberturaParser.Parse(stream);
        var file = Assert.Single(report.Files);

        Assert.Equal(2, file.LinesTotal);
        Assert.Equal(0, file.LinesHit);
        Assert.Equal([1, 2], file.UncoveredLines);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("TRUE")]
    public void Parse_BranchAttribute_IsCaseInsensitive(string branchValue)
    {
        var xml = $"""
                   <?xml version="1.0"?>
                   <coverage><packages><package><classes>
                     <class name="X" filename="x.cs">
                       <lines>
                         <line number="1" hits="1" branch="{branchValue}" condition-coverage="50% (1/2)" />
                       </lines>
                     </class>
                   </classes></package></packages></coverage>
                   """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var report = CoberturaParser.Parse(stream);

        Assert.Equal(2, report.TotalBranches);
        Assert.Equal(1, report.TotalBranchesHit);
    }

    [Fact]
    public void Parse_ClassWithNoLines_ReportsZeroTotals()
    {
        const string xml = """
                           <?xml version="1.0"?>
                           <coverage><packages><package><classes>
                             <class name="Empty" filename="empty.cs" line-rate="0" branch-rate="0">
                             </class>
                           </classes></package></packages></coverage>
                           """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var report = CoberturaParser.Parse(stream);

        Assert.Single(report.Files);
        Assert.Equal(0, report.Files[0].LinesTotal);
        Assert.Equal(1.0, report.Files[0].LineRate);
    }

    // ── Merge ──

    [Fact]
    public void Merge_TwoReports_CombinesFilesByPath()
    {
        var a = new CoverageReport([new FileCoverage("a.cs", 3, 10, 0, 0)]);
        var b = new CoverageReport([new FileCoverage("b.cs", 5, 10, 0, 0)]);

        var merged = CoverageReport.Merge(a, b);

        Assert.Equal(2, merged.Files.Count);
    }

    [Fact]
    public void Merge_SameFile_DedupesLinesByNumberTakingMaxHits()
    {
        // Lines 1-3 covered by both reports; line 4 only by `a`; line 5 only by `b`.
        // Line 2 has hits=0 in `a` and hits=4 in `b` — the union counts it as covered.
        // Branches are on disjoint lines across the two reports, so the per-line union ends
        // up summing them (no overlap to dedupe).
        var a = new FileCoverage("a.cs", LinesHit: 2, LinesTotal: 4, BranchesHit: 1, BranchesTotal: 2)
        {
            LineHits = new Dictionary<int, int> { [1] = 3, [2] = 0, [3] = 5, [4] = 0 },
            BranchesByLine = new Dictionary<int, (int Covered, int Total)> { [1] = (1, 2) }
        };
        var b = new FileCoverage("a.cs", LinesHit: 3, LinesTotal: 4, BranchesHit: 2, BranchesTotal: 4)
        {
            LineHits = new Dictionary<int, int> { [1] = 1, [2] = 4, [3] = 2, [5] = 7 },
            BranchesByLine = new Dictionary<int, (int Covered, int Total)> { [3] = (1, 2), [5] = (1, 2) }
        };

        var merged = CoverageReport.Merge(new CoverageReport([a]), new CoverageReport([b]));

        Assert.Single(merged.Files);
        Assert.Equal(5, merged.Files[0].LinesTotal);   // union {1,2,3,4,5}
        Assert.Equal(4, merged.Files[0].LinesHit);     // {1,2,3,5} covered; 4 still 0
        Assert.Equal(3, merged.Files[0].BranchesHit);   // lines 1, 3, 5 — disjoint
        Assert.Equal(6, merged.Files[0].BranchesTotal); // 2+2+2
    }

    [Fact]
    public void Merge_SameFile_OverlappingBranchLines_DedupesViaMathMax()
    {
        // Both reports carry branch data for the same source line — Codecov-style
        // union-with-max prevents double-counting that would inflate BranchesTotal.
        var a = new FileCoverage("a.cs", 1, 1, 1, 2)
        {
            LineHits = new Dictionary<int, int> { [10] = 1 },
            BranchesByLine = new Dictionary<int, (int Covered, int Total)> { [10] = (1, 2) }
        };
        var b = new FileCoverage("a.cs", 1, 1, 2, 2)
        {
            LineHits = new Dictionary<int, int> { [10] = 5 },
            BranchesByLine = new Dictionary<int, (int Covered, int Total)> { [10] = (2, 2) }
        };

        var merged = a.MergeWith(b);

        Assert.Equal(1, merged.LinesTotal);
        Assert.Equal(2, merged.BranchesHit);    // max(1, 2)
        Assert.Equal(2, merged.BranchesTotal);  // max(2, 2) — not 4
    }

    // ── ParsePath ──

    [Fact]
    public void ParsePath_WithFile_ParsesSuccessfully()
    {
        var report = CoberturaParser.ParsePath(FixturePath);
        Assert.Equal(3, report.Files.Count);
    }

    [Fact]
    public void ParsePath_WithNonexistentPath_Throws()
    {
        Assert.Throws<FileNotFoundException>(() => CoberturaParser.ParsePath("nonexistent"));
    }

    // ── Real-world MTP shape ─────────────────────────────────────────────────
    //
    // Microsoft.Testing.Platform's CodeCoverage extension emits the same line numbers
    // under each `<method><lines>` and once more under the class-level `<lines>`. A
    // source file with nested types or async state machines produces several `<class>`
    // blocks with the same `filename` attribute, each carrying overlapping line numbers.
    //
    // Before #fix-cobertura-method-block-and-line-dedup the parser only saw the first
    // method's `<lines>` of the first class block, dramatically under-counting any file
    // with records, async/await, or expression-trees. The fixtures below mirror what
    // MTP actually produces for `record`-heavy files and let us assert the corrected
    // per-source-line semantics directly.

    [Fact]
    public void Parse_LinesNestedUnderMethods_AreCounted()
    {
        const string xml = """
                           <?xml version="1.0" encoding="utf-8"?>
                           <coverage line-rate="0" branch-rate="0" version="1.0" timestamp="0">
                             <packages><package name="P"><classes>
                               <class name="A" filename="src/A.cs">
                                 <methods>
                                   <method name="Foo" signature="()">
                                     <lines>
                                       <line number="10" hits="2" branch="false" />
                                       <line number="11" hits="2" branch="false" />
                                     </lines>
                                   </method>
                                   <method name="Bar" signature="()">
                                     <lines>
                                       <line number="20" hits="0" branch="false" />
                                     </lines>
                                   </method>
                                 </methods>
                                 <lines>
                                   <line number="10" hits="2" branch="false" />
                                   <line number="11" hits="2" branch="false" />
                                   <line number="20" hits="0" branch="false" />
                                 </lines>
                               </class>
                             </classes></package></packages>
                           </coverage>
                           """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var file = CoberturaParser.Parse(stream).Files.Single();

        Assert.Equal(3, file.LinesTotal);
        Assert.Equal(2, file.LinesHit);
        Assert.Equal([20], file.UncoveredLines);
    }

    [Fact]
    public void Parse_MultipleClassBlocksSameFile_UnionLinesWithMaxHits()
    {
        // Two `<class>` blocks (e.g., the record and its compiler-generated state-machine
        // counterpart) share `filename="src/Dto.cs"`. Line 10 is hit=0 in the first block
        // and hit=5 in the second — the union must report it as covered.
        const string xml = """
                           <?xml version="1.0" encoding="utf-8"?>
                           <coverage line-rate="0" branch-rate="0" version="1.0" timestamp="0">
                             <packages><package name="P"><classes>
                               <class name="Dto" filename="src/Dto.cs">
                                 <lines>
                                   <line number="10" hits="0" branch="false" />
                                   <line number="11" hits="3" branch="false" />
                                 </lines>
                               </class>
                               <class name="Dto+&lt;&gt;d__0" filename="src/Dto.cs">
                                 <lines>
                                   <line number="10" hits="5" branch="false" />
                                   <line number="12" hits="0" branch="false" />
                                 </lines>
                               </class>
                             </classes></package></packages>
                           </coverage>
                           """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var file = CoberturaParser.Parse(stream).Files.Single();

        Assert.Equal("src/Dto.cs", file.Path);
        Assert.Equal(3, file.LinesTotal);                  // union {10, 11, 12}
        Assert.Equal(2, file.LinesHit);                    // 10 (max hits = 5), 11
        Assert.Equal([12], file.UncoveredLines);
        Assert.Equal(5, file.LineHits[10]);                // max-of-block-hits per line
    }
}
