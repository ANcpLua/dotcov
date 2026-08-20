using System.Text;
using System.Xml;
using DotCov.Tests.Infrastructure;
using Xunit;

namespace DotCov.Tests;

public sealed class CoberturaParserTests
{
    private const string FixturePath = "Fixtures/sample.cobertura.xml";

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
    public void Parse_NoBranches_ReportsNoBranchRate()
    {
        var report = CoberturaParser.ParseFile(FixturePath);
        var unused = report.Files.Single(f => f.Path == "src/Unused.cs");

        Assert.Equal(0, unused.BranchesTotal);
        // Not 1.0. A file with no branches has no branch rate - reporting "100%" for absent
        // data is what let a --min-branch gate pass on reports carrying no branch data at all.
        Assert.Null(unused.BranchRate);
        Assert.False(unused.HasBranchData);
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
    public void Evaluate_AboveMinimum_Passes()
    {
        var report = CoberturaParser.ParseFile(FixturePath);
        Assert.Equal(GateOutcome.Pass, report.Evaluate(50).Outcome);
    }

    [Fact]
    public void Evaluate_BelowMinimum_Fails()
    {
        var report = CoberturaParser.ParseFile(FixturePath);
        var gate = report.Evaluate(80);
        Assert.Equal(GateOutcome.Fail, gate.Outcome);
        Assert.Contains("line coverage below threshold", gate.Reason);
    }

    [Fact]
    public void Evaluate_WithBranchMinimum_ChecksBoth()
    {
        var report = CoberturaParser.ParseFile(FixturePath);
        Assert.Equal(GateOutcome.Pass, report.Evaluate(50, 50).Outcome);
        Assert.Equal(GateOutcome.Fail, report.Evaluate(50, 60).Outcome);
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
    public void Parse_XxeEntityReference_Throws()
    {
        // The actual XXE shape: a DTD-declared external entity *referenced* in content.
        // DtdProcessing.Ignore skips the DTD without processing it, so the reference is
        // undeclared and the reader throws — external content can never be pulled in.
        const string malicious = """
                                 <?xml version="1.0"?>
                                 <!DOCTYPE coverage [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
                                 <coverage><packages><package><classes>
                                   <class name="&xxe;" filename="x.cs"><lines>
                                     <line number="1" hits="1" branch="false"/>
                                   </lines></class>
                                 </classes></package></packages></coverage>
                                 """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(malicious));
        Assert.Throws<XmlException>(() => CoberturaParser.Parse(stream));
    }

    [Fact]
    public void Parse_BenignDoctype_ParsesLikeReferenceCobertura()
    {
        // Reference Cobertura, gcovr, and coverage.py emit this DOCTYPE on every report.
        // DtdProcessing.Prohibit rejected the format's canonical emitters; the DOCTYPE must
        // be skipped, not fatal — while the XXE test above stays dead.
        const string canonical = """
                                 <?xml version="1.0"?>
                                 <!DOCTYPE coverage SYSTEM "http://cobertura.sourceforge.net/xml/coverage-04.dtd">
                                 <coverage><packages><package><classes>
                                   <class name="X" filename="x.cs"><lines>
                                     <line number="1" hits="1" branch="false"/>
                                     <line number="2" hits="0" branch="false"/>
                                   </lines></class>
                                 </classes></package></packages></coverage>
                                 """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(canonical));
        var report = CoberturaParser.Parse(stream);

        var file = Assert.Single(report.Files);
        Assert.Equal(2, file.LinesTotal);
        Assert.Equal(1, file.LinesHit);
        Assert.Empty(report.Warnings);
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
        Assert.Null(report.LineRate);
        Assert.False(report.HasLineData);
    }

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
        Assert.Equal([20, 21], file.UncoveredLines);
    }

    [Fact]
    public void Parse_BranchedLines_PopulatesBranchesByLineFromXml()
    {
        const string xml = """
                           <?xml version="1.0"?>
                           <coverage><packages><package><classes>
                             <class name="X" filename="x.cs">
                               <lines>
                                 <line number="10" hits="3" branch="True" condition-coverage="50% (1/2)" />
                                 <line number="20" hits="3" branch="True" condition-coverage="100% (4/4)" />
                                 <line number="30" hits="1" branch="False" />
                               </lines>
                             </class>
                           </classes></package></packages></coverage>
                           """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var file = Assert.Single(CoberturaParser.Parse(stream).Files);

        Assert.Equal(2, file.BranchesByLine.Count);
        Assert.Equal((1, 2), file.BranchesByLine[10]);
        Assert.Equal((4, 4), file.BranchesByLine[20]);
        Assert.False(file.BranchesByLine.ContainsKey(30));
        Assert.Equal(LineStatus.Partial, file.GetLineStatus(10));
        Assert.Equal(LineStatus.Hit, file.GetLineStatus(20));
        Assert.Equal(LineStatus.Hit, file.GetLineStatus(30));
    }

    [Fact]
    public void Parse_LineWithMissingHitsAttribute_TreatsAsZeroWithoutWarning()
    {
        // Absent is not malformed: some emitters omit `hits` on summary lines, so a missing
        // attribute is a plain uncovered line with no warning noise.
        const string xml = """
                           <?xml version="1.0"?>
                           <coverage><packages><package><classes>
                             <class name="X" filename="x.cs">
                               <lines>
                                 <line number="1" branch="False" />
                               </lines>
                             </class>
                           </classes></package></packages></coverage>
                           """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var report = CoberturaParser.Parse(stream);
        var file = Assert.Single(report.Files);

        Assert.Equal(1, file.LinesTotal);
        Assert.Equal(0, file.LinesHit);
        Assert.Equal([1], file.UncoveredLines);
        Assert.Empty(report.Warnings);
    }

    [Theory]
    [InlineData("not-a-number")]
    [InlineData("1.5")]
    public void Parse_UnparseableHits_TreatsAsZeroAndEmitsWarning(string hits)
    {
        // Present-but-unparseable must not silently flip a possibly-covered line to a miss:
        // the line still counts as uncovered (the conservative reading), but the degradation
        // is observable — mirroring the MalformedConditionCoverage pattern.
        var xml = $"""
                   <?xml version="1.0"?>
                   <coverage><packages><package><classes>
                     <class name="X" filename="src/A.cs">
                       <lines>
                         <line number="7" hits="{hits}" branch="False" />
                       </lines>
                     </class>
                   </classes></package></packages></coverage>
                   """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var report = CoberturaParser.Parse(stream);
        var file = Assert.Single(report.Files);

        Assert.Equal(1, file.LinesTotal);
        Assert.Equal(0, file.LinesHit);
        var w = Assert.Single(report.Warnings);
        Assert.Equal(CoverageWarningKind.MalformedHits, w.Kind);
        Assert.Equal("src/A.cs", w.File);
        Assert.Equal(7, w.Line);
        Assert.Contains(hits, w.Detail);
    }

    [Fact]
    public void Parse_HitsAboveIntMax_CountsAsCoveredLine()
    {
        // 64-bit hit counts are real (soak runs; gcovr/llvm-cov/JaCoCo converters use long
        // counters and the Cobertura DTD does not bound hits). Overflow must saturate, not
        // degrade to 0 — degrading silently flipped a covered line to a miss and deflated
        // line coverage below a gate it genuinely cleared.
        const string xml = """
                           <?xml version="1.0"?>
                           <coverage><packages><package><classes>
                             <class name="X" filename="x.cs">
                               <lines>
                                 <line number="1" hits="3000000000" branch="False" />
                                 <line number="2" hits="1" branch="False" />
                               </lines>
                             </class>
                           </classes></package></packages></coverage>
                           """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var report = CoberturaParser.Parse(stream);
        var file = Assert.Single(report.Files);

        Assert.Equal(2, file.LinesTotal);
        Assert.Equal(2, file.LinesHit);
        Assert.Equal(1.0, file.LineRate);
        Assert.Equal(int.MaxValue, file.LineHits[1]);   // saturated, still "covered"
        Assert.Empty(report.Warnings);
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
        // A class the emitter listed but recorded no lines for is unmeasured, not fully covered.
        Assert.Null(report.Files[0].LineRate);
    }

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
        var a = Reports.ClassifiedFile("a.cs", linesHit: 2, linesTotal: 4, branchesHit: 1, branchesTotal: 2,
            lineHits: new Dictionary<int, int> { [1] = 3, [2] = 0, [3] = 5, [4] = 0 },
            branchesByLine: new Dictionary<int, (int Covered, int Total)> { [1] = (1, 2) });
        var b = Reports.ClassifiedFile("a.cs", linesHit: 3, linesTotal: 4, branchesHit: 2, branchesTotal: 4,
            lineHits: new Dictionary<int, int> { [1] = 1, [2] = 4, [3] = 2, [5] = 7 },
            branchesByLine: new Dictionary<int, (int Covered, int Total)> { [3] = (1, 2), [5] = (1, 2) });

        var merged = CoverageReport.Merge(new CoverageReport([a]), new CoverageReport([b]));

        Assert.Single(merged.Files);
        Assert.Equal(5, merged.Files[0].LinesTotal);
        Assert.Equal(4, merged.Files[0].LinesHit);
        Assert.Equal(3, merged.Files[0].BranchesHit);
        Assert.Equal(6, merged.Files[0].BranchesTotal);
    }

    [Fact]
    public void Merge_SameFile_OverlappingBranchLines_DedupesViaMathMax()
    {
        var a = Reports.ClassifiedFile("a.cs", 1, 1, 1, 2,
            lineHits: new Dictionary<int, int> { [10] = 1 },
            branchesByLine: new Dictionary<int, (int Covered, int Total)> { [10] = (1, 2) });
        var b = Reports.ClassifiedFile("a.cs", 1, 1, 2, 2,
            lineHits: new Dictionary<int, int> { [10] = 5 },
            branchesByLine: new Dictionary<int, (int Covered, int Total)> { [10] = (2, 2) });

        var (merged, _) = a.MergeWith(b);

        Assert.Equal(1, merged.LinesTotal);
        Assert.Equal(2, merged.BranchesHit);
        Assert.Equal(2, merged.BranchesTotal);
    }

    [Fact]
    public void Merge_SplitConditionRuns_UnionsByConditionNumber_NotLineLevelMax()
    {
        // Two runs cover DIFFERENT conditions of the same branched line. Coverlet exposes
        // per-branch identity (<condition number= coverage=>), so the true union is 5/6 — a
        // line-level Math.Max on the (3/6) counts wrongly reports 3/6 (the false not-hit).
        // This is the exact case that shipped broken because no test covered split runs.
        var a = Cobertura.NewDoc()
            .AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "50% (3/6)",
                (1, "100%"), (2, "50%"), (3, "0%")))
            .Parse();
        var b = Cobertura.NewDoc()
            .AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "50% (3/6)",
                (1, "0%"), (2, "50%"), (3, "100%")))
            .Parse();

        var merged = CoverageReport.Merge(a, b);

        Assert.Equal(5, merged.TotalBranchesHit);
        Assert.Equal(6, merged.TotalBranches);
    }

    [Fact]
    public void Parse_UnparseableConditionCoverage_IgnoresConditionKeepsLineAggregate()
    {
        // A coverlet emitter regression (garbage `coverage`) must not crash and must not poison
        // the per-condition map — the line still parses via its line-level aggregate.
        var f = Cobertura.NewDoc()
            .AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "50% (1/2)", (1, "garbage")))
            .Parse().Files[0];

        Assert.Equal(1, f.BranchesHit);            // aggregate (1/2) preserved
        Assert.Equal(2, f.BranchesTotal);
        Assert.Empty(f.ConditionsByLine);          // the unparseable condition was dropped
    }

    [Fact]
    public void Parse_ConditionCountInconsistentWithAggregate_DropsConditionDetail()
    {
        // 1 condition but the line aggregate reports 4 outcomes (a switch jump-table). 1*2 != 4,
        // so the 2-outcome reconstruction is unsafe — drop to the aggregate rather than invent a total.
        var f = Cobertura.NewDoc()
            .AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "25% (1/4)", (1, "50%")))
            .Parse().Files[0];

        Assert.Equal(4, f.BranchesTotal);          // aggregate (1/4) preserved
        Assert.Empty(f.ConditionsByLine);          // gate dropped the inconsistent detail
    }

    [Fact]
    public void Parse_SameConditionUnderMultipleClassBlocks_DedupesViaMathMax()
    {
        // Coverlet emits the same line under <method><lines> AND <class><lines>; the per-condition
        // covered count must Math.Max across blocks, not sum (which would over-count the branch).
        var f = Cobertura.NewDoc()
            .AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "50% (1/2)", (1, "50%")))
            .AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "100% (2/2)", (1, "100%")))
            .Parse().Files[0];

        Assert.Equal(2, f.ConditionsByLine[10][1]);   // Math.Max(1, 2), not 1 + 2
    }

    [Fact]
    public void Merge_MismatchedConditionNumberSets_FallsBackToLineAggregateAndWarns()
    {
        // Coverlet condition `number`s are IL branch offsets — stable only for the identical
        // assembly build. When the two sides' number sets for a line differ, they no longer
        // identify the same branches, and unioning them would invent a branch total neither
        // emitter reported. The merge must fall back to the line-level Math.Max and warn.
        var a = Cobertura.NewDoc()
            .AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "50% (2/4)", (1, "100%"), (2, "0%")))
            .Parse();
        var b = Cobertura.NewDoc()
            .AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "66.66% (4/6)", (1, "0%"), (2, "100%"), (3, "100%")))
            .Parse();

        var merged = CoverageReport.Merge(a, b);
        var f = merged.Files[0];

        Assert.Equal(4, f.BranchesHit);    // line-level Math.Max of (2/4) and (4/6)
        Assert.Equal(6, f.BranchesTotal);
        // The untrustworthy identity is not merely dropped but POISONED: the empty sentinel
        // entry marks the line so no later merge can resurrect one side's detail and make
        // the aggregate depend on fold order (see MergeConditionIdentityTests).
        Assert.Empty(f.ConditionsByLine[10]);
        Assert.Contains(merged.Warnings, w =>
            w.Kind is CoverageWarningKind.ConditionIdentityMismatch && w.Line == 10);
    }

    [Fact]
    public void Merge_DisjointConditionNumbersSameTotal_DoesNotInventBranchTotal()
    {
        // The Debug-vs-Release repro: the same physical 2-way branch gets condition number 0
        // in one build and 139 in the other, with identical totals. The old per-number union
        // produced branchesHit=2 of branchesTotal=4 — a total NEITHER emitter ever reported,
        // with no warning (totals matched, so BranchTotalMismatch never fired). The true
        // union is 2/2.
        var a = Cobertura.NewDoc()
            .AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "100% (2/2)", (0, "100%")))
            .Parse();
        var b = Cobertura.NewDoc()
            .AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "0% (0/2)", (139, "0%")))
            .Parse();

        var merged = CoverageReport.Merge(a, b);
        var f = merged.Files[0];

        Assert.Equal(2, f.BranchesHit);
        Assert.Equal(2, f.BranchesTotal);
        Assert.Contains(merged.Warnings, w =>
            w.Kind is CoverageWarningKind.ConditionIdentityMismatch && w.Line == 10);
    }

    [Fact]
    public void Merge_OneSidedConditionDetail_SurvivesAndNeverRegressesTheAggregate()
    {
        // Report `a` carries per-condition detail; `b` (a different emitter) ships only the
        // line aggregate. The detail must be carried forward — an intersection would erase it,
        // silently downgrading every later merge to line-level Math.Max — and the aggregate
        // must keep the higher line-level count `b` observed.
        var a = Cobertura.NewDoc()
            .AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "25% (1/4)", (1, "50%"), (2, "0%")))
            .Parse();
        var b = Cobertura.NewDoc()
            .AddClass("src/Foo.cs", c => c.Branch(10, "50% (2/4)"))
            .Parse();

        var f = CoverageReport.Merge(a, b).Files[0];

        Assert.True(f.ConditionsByLine.ContainsKey(10));    // detail survives for future merges
        Assert.Equal(1, f.ConditionsByLine[10][1]);
        Assert.Equal(0, f.ConditionsByLine[10][2]);
        Assert.Equal(2, f.BranchesHit);                     // b's line-level (2/4) not regressed
        Assert.Equal(4, f.BranchesTotal);
    }

    [Fact]
    public void Merge_ThreeReports_MiddleWithoutConditionDetail_IsOrderIndependent()
    {
        // Merge(Merge(A,B),C) must equal Merge(A,Merge(B,C)) even when B carries no condition
        // detail: A and C each exercised a DIFFERENT condition of line 10, so the true union
        // is 3/4 — reachable in every merge order only because one-sided detail is carried
        // forward instead of intersected away.
        CoverageReport A() => Cobertura.NewDoc()
            .AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "25% (1/4)", (1, "50%"), (2, "0%")))
            .Parse();
        CoverageReport B() => Cobertura.NewDoc()
            .AddClass("src/Foo.cs", c => c.Branch(10, "25% (1/4)"))
            .Parse();
        CoverageReport C() => Cobertura.NewDoc()
            .AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "50% (2/4)", (1, "0%"), (2, "100%")))
            .Parse();

        var leftFold = CoverageReport.Merge(CoverageReport.Merge(A(), B()), C()).Files[0];
        var rightFold = CoverageReport.Merge(A(), CoverageReport.Merge(B(), C())).Files[0];

        Assert.Equal(3, leftFold.BranchesHit);      // #1 max(1,0)=1, #2 max(0,2)=2
        Assert.Equal(4, leftFold.BranchesTotal);
        Assert.Equal(leftFold.BranchesHit, rightFold.BranchesHit);
        Assert.Equal(leftFold.BranchesTotal, rightFold.BranchesTotal);
    }

    [Theory]
    [InlineData("200%")]
    [InlineData("-50%")]
    [InlineData("NaN%")]
    public void Parse_OutOfRangeConditionCoverage_DropsConditionKeepsLineAggregate(string coverage)
    {
        // A condition percent outside [0,100] cannot describe a 2-way jump. Left unclamped,
        // coverage="200%" recorded covered=4 for one condition, and a later merge recompute
        // reported BranchesHit=4 of BranchesTotal=2 — a >100% branch rate. The bogus
        // condition is dropped; the line-level aggregate stays authoritative.
        var report = Cobertura.NewDoc()
            .AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "100% (2/2)", (1, coverage)))
            .Parse();
        var f = report.Files[0];

        Assert.Equal(2, f.BranchesHit);
        Assert.Equal(2, f.BranchesTotal);
        Assert.Empty(f.ConditionsByLine);

        // And the invariant the clamp protects: merging two such reports can never push
        // BranchesHit past BranchesTotal.
        var again = Cobertura.NewDoc()
            .AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "100% (2/2)", (1, coverage)))
            .Parse();
        var merged = CoverageReport.Merge(report, again).Files[0];
        Assert.True(merged.BranchesHit <= merged.BranchesTotal);
        Assert.Equal(2, merged.BranchesHit);
        Assert.Equal(2, merged.BranchesTotal);
    }

    [Fact]
    public void Parse_MalformedAndStrayConditions_AreIgnored_KeepsLineAggregate()
    {
        // Robustness against bad emitter output: a <condition> outside any branched line, one with
        // a non-numeric `number`, and one missing its `coverage` must all be ignored — no crash,
        // and the branched line still parses via its line-level aggregate.
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <coverage line-rate="0" branch-rate="0" version="1.0">
              <packages><package name="P"><classes>
                <class name="P.Foo" filename="src/Foo.cs">
                  <lines>
                    <line number="1" hits="1" branch="false" />
                    <condition number="9" coverage="50%" />
                    <line number="2" hits="1" branch="true" condition-coverage="50% (2/4)">
                      <conditions>
                        <condition number="x" coverage="50%" />
                        <condition number="2" />
                      </conditions>
                    </line>
                  </lines>
                </class>
              </classes></package></packages>
            </coverage>
            """;

        var f = CoberturaParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml))).Files[0];

        Assert.Equal(2, f.BranchesHit);     // line-level aggregate (2/4) preserved
        Assert.Equal(4, f.BranchesTotal);
        Assert.Empty(f.ConditionsByLine);   // stray / non-numeric / coverage-less conditions all dropped
    }

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

    [Fact]
    public void Parse_WellFormedXml_EmitsNoWarnings()
    {
        const string xml = """
                           <?xml version="1.0"?>
                           <coverage><packages><package><classes>
                             <class name="X" filename="x.cs">
                               <lines>
                                 <line number="1" hits="1" branch="True" condition-coverage="100% (2/2)" />
                               </lines>
                             </class>
                           </classes></package></packages></coverage>
                           """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var report = CoberturaParser.Parse(stream);

        Assert.Empty(report.Warnings);
    }

    [Fact]
    public void Parse_MalformedConditionString_EmitsWarning()
    {
        const string xml = """
                           <?xml version="1.0"?>
                           <coverage><packages><package><classes>
                             <class name="X" filename="src/A.cs">
                               <lines>
                                 <line number="42" hits="1" branch="True" condition-coverage="garbage" />
                               </lines>
                             </class>
                           </classes></package></packages></coverage>
                           """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var report = CoberturaParser.Parse(stream);

        Assert.Equal(0, report.Files[0].BranchesTotal);
        var w = Assert.Single(report.Warnings);
        Assert.Equal(CoverageWarningKind.MalformedConditionCoverage, w.Kind);
        Assert.Equal("src/A.cs", w.File);
        Assert.Equal(42, w.Line);
        Assert.Contains("garbage", w.Detail);
    }

    [Theory]
    [InlineData("50% (99999999999999/2)")]
    [InlineData("50% (1/99999999999999)")]
    public void Parse_ConditionCoverageWithIntOverflow_EmitsWarning(string condition)
    {
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

        // The overflowing branch entry is dropped (not silently zeroed INTO the totals)...
        Assert.Equal(0, report.Files[0].BranchesTotal);
        // ...and the drop is observable as a structured warning.
        var w = Assert.Single(report.Warnings);
        Assert.Equal(CoverageWarningKind.MalformedConditionCoverage, w.Kind);
        Assert.Contains(condition, w.Detail);
    }

    [Fact]
    public async Task ParseAsync_MalformedConditionString_EmitsWarning()
    {
        const string xml = """
                           <?xml version="1.0"?>
                           <coverage><packages><package><classes>
                             <class name="X" filename="x.cs">
                               <lines>
                                 <line number="3" hits="1" branch="True" condition-coverage="???" />
                               </lines>
                             </class>
                           </classes></package></packages></coverage>
                           """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var report = await CoberturaParser.ParseAsync(stream);

        var w = Assert.Single(report.Warnings);
        Assert.Equal(CoverageWarningKind.MalformedConditionCoverage, w.Kind);
        Assert.Equal(3, w.Line);
    }

    [Fact]
    public void Parse_MultipleClassBlocksSameFile_UnionLinesWithMaxHits()
    {
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
        Assert.Equal(3, file.LinesTotal);
        Assert.Equal(2, file.LinesHit);
        Assert.Equal([12], file.UncoveredLines);
        Assert.Equal(5, file.LineHits[10]);
    }

    // ── Path identity: separator normalization and case-insensitivity ──
    // The normalized filename is the file's merge-identity key across Windows/Linux CI jobs;
    // these pin the contract the ConsumeClass comment declares.

    [Fact]
    public void Parse_BackslashFilename_NormalizesToForwardSlashPath()
    {
        var report = Cobertura.NewDoc()
            .AddClass(@"src\App\A.cs", c => c.Line(1, hits: 1))
            .Parse();

        var file = Assert.Single(report.Files);
        Assert.Equal("src/App/A.cs", file.Path);
    }

    [Fact]
    public void Merge_BackslashAndForwardSlashReports_UnionAsOneFile()
    {
        // The exact Windows+Linux CI matrix scenario: coverlet on Windows writes `src\A.cs`,
        // on Linux `src/A.cs`. The merged report must union their lines as one file, not
        // count the same source file twice.
        var windows = Cobertura.NewDoc()
            .AddClass(@"src\A.cs", c => c.Line(1, hits: 1).Line(2, hits: 0))
            .Parse();
        var linux = Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c.Line(2, hits: 3).Line(3, hits: 0))
            .Parse();

        var merged = CoverageReport.Merge(windows, linux);

        var file = Assert.Single(merged.Files);
        Assert.Equal(3, file.LinesTotal);
        Assert.Equal(2, file.LinesHit);   // 1 from windows, 2 from linux
        Assert.Equal([3], file.UncoveredLines);
    }

    [Fact]
    public void Parse_ClassBlocksDifferingOnlyInPathCase_StayDistinctFiles()
    {
        // File identity is Ordinal: case-differing filenames are genuinely distinct files on
        // the case-sensitive filesystems Cobertura's native emitters run on —
        // linux/net/netfilter really contains both xt_TCPMSS.c and xt_tcpmss.c. The old
        // OrdinalIgnoreCase keying fused such pairs via Math.Max, silently erasing the
        // fully-uncovered file's misses and reporting 100% where the truth is 50%.
        var report = Cobertura.NewDoc()
            .AddClass("net/netfilter/xt_TCPMSS.c", c => c.Line(1, hits: 1).Line(2, hits: 1))
            .AddClass("net/netfilter/xt_tcpmss.c", c => c.Line(1, hits: 0).Line(2, hits: 0))
            .Parse();

        Assert.Equal(2, report.Files.Count);
        Assert.Equal(4, report.TotalLines);
        Assert.Equal(2, report.TotalLinesHit);
        Assert.Equal(0.5, report.LineRate);
        var uncoveredFile = report.Files.Single(f => f.Path == "net/netfilter/xt_tcpmss.c");
        Assert.Equal(0.0, uncoveredFile.LineRate);
    }
}
