using TUnit.Assertions.Enums;
using System.Text;
using System.Xml;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

public sealed class CoberturaParserTests
{
    private const string FixturePath = "Fixtures/sample.cobertura.xml";

    [Test]
    public async Task Parse_FullyCoveredClass_ReportsAllLinesHit()
    {
        var report = CoberturaParser.Parse(ReportInput.FromFile(FixturePath));
        var calculator = report.Files.Single(f => f.Path == "src/Calculator.cs");

        await Assert.That(calculator.LinesTotal).IsEqualTo(4);
        await Assert.That(calculator.LinesHit).IsEqualTo(4);
        await Assert.That(calculator.LineRate).IsEqualTo(1.0);
    }

    [Test]
    public async Task Parse_PartiallyCoveredClass_ReportsCorrectHitCount()
    {
        var report = CoberturaParser.Parse(ReportInput.FromFile(FixturePath));
        var parser = report.Files.Single(f => f.Path == "src/Parser.cs");

        await Assert.That(parser.LinesTotal).IsEqualTo(5);
        await Assert.That(parser.LinesHit).IsEqualTo(3);
        await Assert.That(parser.LineRate).IsEqualTo(0.6);
    }

    [Test]
    public async Task Parse_UncoveredClass_ReportsZeroLineRate()
    {
        var report = CoberturaParser.Parse(ReportInput.FromFile(FixturePath));
        var unused = report.Files.Single(f => f.Path == "src/Unused.cs");

        await Assert.That(unused.LinesTotal).IsEqualTo(3);
        await Assert.That(unused.LinesHit).IsEqualTo(0);
        await Assert.That(unused.LineRate).IsEqualTo(0.0);
    }

    [Test]
    public async Task Parse_FullBranchCoverage_ReportsAllBranchesHit()
    {
        var report = CoberturaParser.Parse(ReportInput.FromFile(FixturePath));
        var calculator = report.Files.Single(f => f.Path == "src/Calculator.cs");

        await Assert.That(calculator.BranchesTotal).IsEqualTo(2);
        await Assert.That(calculator.BranchesHit).IsEqualTo(2);
        await Assert.That(calculator.BranchRate).IsEqualTo(1.0);
    }

    [Test]
    public async Task Parse_PartialBranches_ExtractsConditionCoverageCorrectly()
    {
        var report = CoberturaParser.Parse(ReportInput.FromFile(FixturePath));
        var parser = report.Files.Single(f => f.Path == "src/Parser.cs");

        await Assert.That(parser.BranchesTotal).IsEqualTo(4);
        await Assert.That(parser.BranchesHit).IsEqualTo(1);
        await Assert.That(parser.BranchRate).IsEqualTo(0.25);
    }

    [Test]
    public async Task Parse_NoBranches_ReportsNoBranchRate()
    {
        var report = CoberturaParser.Parse(ReportInput.FromFile(FixturePath));
        var unused = report.Files.Single(f => f.Path == "src/Unused.cs");

        await Assert.That(unused.BranchesTotal).IsEqualTo(0);
        // Not 1.0. A file with no branches has no branch rate - reporting "100%" for absent
        // data is what let a --min-branch gate pass on reports carrying no branch data at all.
        await Assert.That(unused.BranchRate).IsNull();
        await Assert.That(unused.HasBranchData).IsFalse();
    }

    [Test]
    public async Task Report_AggregateTotals_SumsAcrossAllFiles()
    {
        var report = CoberturaParser.Parse(ReportInput.FromFile(FixturePath));

        await Assert.That(report.TotalLines).IsEqualTo(12);
        await Assert.That(report.TotalLinesHit).IsEqualTo(7);
        await Assert.That(report.TotalBranches).IsEqualTo(6);
        await Assert.That(report.TotalBranchesHit).IsEqualTo(3);
    }

    [Test]
    public async Task Evaluate_AboveMinimum_Passes()
    {
        var report = CoberturaParser.Parse(ReportInput.FromFile(FixturePath));
        await Assert.That(report.Evaluate(50).Outcome).IsEqualTo(GateOutcome.Pass);
    }

    [Test]
    public async Task Evaluate_BelowMinimum_Fails()
    {
        var report = CoberturaParser.Parse(ReportInput.FromFile(FixturePath));
        var gate = report.Evaluate(80);
        await Assert.That(gate.Outcome).IsEqualTo(GateOutcome.Fail);
        await Assert.That(gate.Reason).Contains("line coverage below threshold");
    }

    [Test]
    public async Task Evaluate_WithBranchMinimum_ChecksBoth()
    {
        var report = CoberturaParser.Parse(ReportInput.FromFile(FixturePath));
        await Assert.That(report.Evaluate(50, 50).Outcome).IsEqualTo(GateOutcome.Pass);
        await Assert.That(report.Evaluate(50, 60).Outcome).IsEqualTo(GateOutcome.Fail);
    }

    [Test]
    public async Task BelowPercent_ReturnsOnlyFilesUnderThreshold()
    {
        var report = CoberturaParser.Parse(ReportInput.FromFile(FixturePath));
        var below80 = report.BelowPercent(80).ToList();

        await Assert.That(below80.Count).IsEqualTo(2);
        await Assert.That(below80).Contains(f => f.Path == "src/Parser.cs");
        await Assert.That(below80).Contains(f => f.Path == "src/Unused.cs");
        await Assert.That(below80).DoesNotContain(f => f.Path == "src/Calculator.cs");
    }

    [Test]
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
        Assert.ThrowsExactly<XmlException>(() => CoberturaParser.Parse(stream));
    }

    [Test]
    public async Task Parse_BenignDoctype_ParsesLikeReferenceCobertura()
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

        var file = await Assert.That(report.Files).HasSingleItem();
        await Assert.That(file.LinesTotal).IsEqualTo(2);
        await Assert.That(file.LinesHit).IsEqualTo(1);
        await Assert.That(report.Warnings).IsEmpty();
    }

    [Test]
    public async Task Parse_EmptyPackages_ReturnsEmptyReport()
    {
        const string xml = """
                           <?xml version="1.0"?>
                           <coverage><packages></packages></coverage>
                           """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var report = CoberturaParser.Parse(stream);

        await Assert.That(report.Files).IsEmpty();
        await Assert.That(report.LineRate).IsNull();
        await Assert.That(report.HasLineData).IsFalse();
    }

    [Test]
    public async Task Parse_CoverletLayout_DedupesMethodsAndClassLines()
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
        var file = await Assert.That(report.Files).HasSingleItem();

        await Assert.That(file.LinesTotal).IsEqualTo(4);
        await Assert.That(file.LinesHit).IsEqualTo(2);
        await Assert.That(file.BranchesTotal).IsEqualTo(4);
        await Assert.That(file.BranchesHit).IsEqualTo(1);
        await Assert.That(file.UncoveredLines).IsEquivalentTo([20, 21], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Parse_BranchedLines_PopulatesBranchesByLineFromXml()
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
        var file = await Assert.That(CoberturaParser.Parse(stream).Files).HasSingleItem();

        await Assert.That(file.BranchesByLine.Count).IsEqualTo(2);
        await Assert.That(file.BranchesByLine[10]).IsEqualTo((1, 2));
        await Assert.That(file.BranchesByLine[20]).IsEqualTo((4, 4));
        await Assert.That(file.BranchesByLine.ContainsKey(30)).IsFalse();
        await Assert.That(file.GetLineStatus(10)).IsEqualTo(LineStatus.Partial);
        await Assert.That(file.GetLineStatus(20)).IsEqualTo(LineStatus.Hit);
        await Assert.That(file.GetLineStatus(30)).IsEqualTo(LineStatus.Hit);
    }

    [Test]
    public async Task Parse_LineWithMissingHitsAttribute_TreatsAsZeroWithoutWarning()
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
        var file = await Assert.That(report.Files).HasSingleItem();

        await Assert.That(file.LinesTotal).IsEqualTo(1);
        await Assert.That(file.LinesHit).IsEqualTo(0);
        await Assert.That(file.UncoveredLines).IsEquivalentTo([1], CollectionOrdering.Matching);
        await Assert.That(report.Warnings).IsEmpty();
    }

    [Test]
    [Arguments("not-a-number")]
    [Arguments("1.5")]
    public async Task Parse_UnparseableHits_TreatsAsZeroAndEmitsWarning(string hits)
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
        var file = await Assert.That(report.Files).HasSingleItem();

        await Assert.That(file.LinesTotal).IsEqualTo(1);
        await Assert.That(file.LinesHit).IsEqualTo(0);
        var w = await Assert.That(report.Warnings).HasSingleItem();
        await Assert.That(w.Kind).IsEqualTo(CoverageWarningKind.MalformedHits);
        await Assert.That(w.File).IsEqualTo("src/A.cs");
        await Assert.That(w.Line).IsEqualTo(7);
        await Assert.That(w.Detail).Contains(hits);
    }

    [Test]
    public async Task Parse_HitsAboveIntMax_CountsAsCoveredLine()
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
        var file = await Assert.That(report.Files).HasSingleItem();

        await Assert.That(file.LinesTotal).IsEqualTo(2);
        await Assert.That(file.LinesHit).IsEqualTo(2);
        await Assert.That(file.LineRate).IsEqualTo(1.0);
        await Assert.That(file.LineHits[1]).IsEqualTo(int.MaxValue);   // saturated, still "covered"
        await Assert.That(report.Warnings).IsEmpty();
    }

    [Test]
    [Arguments("true")]
    [Arguments("True")]
    [Arguments("TRUE")]
    public async Task Parse_BranchAttribute_IsCaseInsensitive(string branchValue)
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

        await Assert.That(report.TotalBranches).IsEqualTo(2);
        await Assert.That(report.TotalBranchesHit).IsEqualTo(1);
    }

    [Test]
    public async Task Parse_ClassWithNoLines_ReportsZeroTotals()
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

        await Assert.That(report.Files).HasSingleItem();
        await Assert.That(report.Files[0].LinesTotal).IsEqualTo(0);
        // A class the emitter listed but recorded no lines for is unmeasured, not fully covered.
        await Assert.That(report.Files[0].LineRate).IsNull();
    }

    [Test]
    public async Task Merge_TwoReports_CombinesFilesByPath()
    {
        var a = new CoverageReport([new FileCoverage("a.cs", 3, 10, 0, 0)]);
        var b = new CoverageReport([new FileCoverage("b.cs", 5, 10, 0, 0)]);

        var merged = CoverageReport.Merge(a, b);

        await Assert.That(merged.Files.Count).IsEqualTo(2);
    }

    [Test]
    public async Task Merge_SameFile_DedupesLinesByNumberTakingMaxHits()
    {
        var a = Reports.ClassifiedFile("a.cs", linesHit: 2, linesTotal: 4, branchesHit: 1, branchesTotal: 2,
            lineHits: new Dictionary<int, int> { [1] = 3, [2] = 0, [3] = 5, [4] = 0 },
            branchesByLine: new Dictionary<int, (int Covered, int Total)> { [1] = (1, 2) });
        var b = Reports.ClassifiedFile("a.cs", linesHit: 3, linesTotal: 4, branchesHit: 2, branchesTotal: 4,
            lineHits: new Dictionary<int, int> { [1] = 1, [2] = 4, [3] = 2, [5] = 7 },
            branchesByLine: new Dictionary<int, (int Covered, int Total)> { [3] = (1, 2), [5] = (1, 2) });

        var merged = CoverageReport.Merge(new CoverageReport([a]), new CoverageReport([b]));

        await Assert.That(merged.Files).HasSingleItem();
        await Assert.That(merged.Files[0].LinesTotal).IsEqualTo(5);
        await Assert.That(merged.Files[0].LinesHit).IsEqualTo(4);
        await Assert.That(merged.Files[0].BranchesHit).IsEqualTo(3);
        await Assert.That(merged.Files[0].BranchesTotal).IsEqualTo(6);
    }

    [Test]
    public async Task Merge_SameFile_OverlappingBranchLines_DedupesViaMathMax()
    {
        var a = Reports.ClassifiedFile("a.cs", 1, 1, 1, 2,
            lineHits: new Dictionary<int, int> { [10] = 1 },
            branchesByLine: new Dictionary<int, (int Covered, int Total)> { [10] = (1, 2) });
        var b = Reports.ClassifiedFile("a.cs", 1, 1, 2, 2,
            lineHits: new Dictionary<int, int> { [10] = 5 },
            branchesByLine: new Dictionary<int, (int Covered, int Total)> { [10] = (2, 2) });

        var (merged, _) = a.MergeWith(b);

        await Assert.That(merged.LinesTotal).IsEqualTo(1);
        await Assert.That(merged.BranchesHit).IsEqualTo(2);
        await Assert.That(merged.BranchesTotal).IsEqualTo(2);
    }

    [Test]
    public async Task Merge_SplitConditionRuns_UnionsByConditionNumber_NotLineLevelMax()
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

        await Assert.That(merged.TotalBranchesHit).IsEqualTo(5);
        await Assert.That(merged.TotalBranches).IsEqualTo(6);
    }

    [Test]
    public async Task Parse_UnparseableConditionCoverage_IgnoresConditionKeepsLineAggregate()
    {
        // A coverlet emitter regression (garbage `coverage`) must not crash and must not poison
        // the per-condition map — the line still parses via its line-level aggregate.
        var f = Cobertura.NewDoc()
            .AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "50% (1/2)", (1, "garbage")))
            .Parse().Files[0];

        await Assert.That(f.BranchesHit).IsEqualTo(1);            // aggregate (1/2) preserved
        await Assert.That(f.BranchesTotal).IsEqualTo(2);
        await Assert.That(f.ConditionsByLine).IsEmpty();          // the unparseable condition was dropped
    }

    [Test]
    public async Task Parse_ConditionCountInconsistentWithAggregate_DropsConditionDetail()
    {
        // 1 condition but the line aggregate reports 4 outcomes (a switch jump-table). 1*2 != 4,
        // so the 2-outcome reconstruction is unsafe — drop to the aggregate rather than invent a total.
        var f = Cobertura.NewDoc()
            .AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "25% (1/4)", (1, "50%")))
            .Parse().Files[0];

        await Assert.That(f.BranchesTotal).IsEqualTo(4);          // aggregate (1/4) preserved
        await Assert.That(f.ConditionsByLine).IsEmpty();          // gate dropped the inconsistent detail
    }

    [Test]
    public async Task Parse_SameConditionUnderMultipleClassBlocks_DedupesViaMathMax()
    {
        // Coverlet emits the same line under <method><lines> AND <class><lines>; the per-condition
        // covered count must Math.Max across blocks, not sum (which would over-count the branch).
        var f = Cobertura.NewDoc()
            .AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "50% (1/2)", (1, "50%")))
            .AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "100% (2/2)", (1, "100%")))
            .Parse().Files[0];

        await Assert.That(f.ConditionsByLine[10][1]).IsEqualTo(2);   // Math.Max(1, 2), not 1 + 2
    }

    [Test]
    public async Task Merge_MismatchedConditionNumberSets_FallsBackToLineAggregateAndWarns()
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

        await Assert.That(f.BranchesHit).IsEqualTo(4);    // line-level Math.Max of (2/4) and (4/6)
        await Assert.That(f.BranchesTotal).IsEqualTo(6);
        // The untrustworthy identity is not merely dropped but POISONED: the empty sentinel
        // entry marks the line so no later merge can resurrect one side's detail and make
        // the aggregate depend on fold order (see MergeConditionIdentityTests).
        await Assert.That(f.ConditionsByLine[10]).IsEmpty();
        await Assert.That(merged.Warnings).Contains(w =>
            w.Kind is CoverageWarningKind.ConditionIdentityMismatch && w.Line == 10);
    }

    [Test]
    public async Task Merge_DisjointConditionNumbersSameTotal_DoesNotInventBranchTotal()
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

        await Assert.That(f.BranchesHit).IsEqualTo(2);
        await Assert.That(f.BranchesTotal).IsEqualTo(2);
        await Assert.That(merged.Warnings).Contains(w =>
            w.Kind is CoverageWarningKind.ConditionIdentityMismatch && w.Line == 10);
    }

    [Test]
    public async Task Merge_OneSidedConditionDetail_SurvivesAndNeverRegressesTheAggregate()
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

        await Assert.That(f.ConditionsByLine.ContainsKey(10)).IsTrue();    // detail survives for future merges
        await Assert.That(f.ConditionsByLine[10][1]).IsEqualTo(1);
        await Assert.That(f.ConditionsByLine[10][2]).IsEqualTo(0);
        await Assert.That(f.BranchesHit).IsEqualTo(2);                     // b's line-level (2/4) not regressed
        await Assert.That(f.BranchesTotal).IsEqualTo(4);
    }

    [Test]
    public async Task Merge_ThreeReports_MiddleWithoutConditionDetail_IsOrderIndependent()
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

        await Assert.That(leftFold.BranchesHit).IsEqualTo(3);      // #1 max(1,0)=1, #2 max(0,2)=2
        await Assert.That(leftFold.BranchesTotal).IsEqualTo(4);
        await Assert.That(rightFold.BranchesHit).IsEqualTo(leftFold.BranchesHit);
        await Assert.That(rightFold.BranchesTotal).IsEqualTo(leftFold.BranchesTotal);
    }

    [Test]
    [Arguments("200%")]
    [Arguments("-50%")]
    [Arguments("NaN%")]
    public async Task Parse_OutOfRangeConditionCoverage_DropsConditionKeepsLineAggregate(string coverage)
    {
        // A condition percent outside [0,100] cannot describe a 2-way jump. Left unclamped,
        // coverage="200%" recorded covered=4 for one condition, and a later merge recompute
        // reported BranchesHit=4 of BranchesTotal=2 — a >100% branch rate. The bogus
        // condition is dropped; the line-level aggregate stays authoritative.
        var report = Cobertura.NewDoc()
            .AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "100% (2/2)", (1, coverage)))
            .Parse();
        var f = report.Files[0];

        await Assert.That(f.BranchesHit).IsEqualTo(2);
        await Assert.That(f.BranchesTotal).IsEqualTo(2);
        await Assert.That(f.ConditionsByLine).IsEmpty();

        // And the invariant the clamp protects: merging two such reports can never push
        // BranchesHit past BranchesTotal.
        var again = Cobertura.NewDoc()
            .AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "100% (2/2)", (1, coverage)))
            .Parse();
        var merged = CoverageReport.Merge(report, again).Files[0];
        await Assert.That(merged.BranchesHit <= merged.BranchesTotal).IsTrue();
        await Assert.That(merged.BranchesHit).IsEqualTo(2);
        await Assert.That(merged.BranchesTotal).IsEqualTo(2);
    }

    [Test]
    public async Task Parse_MalformedAndStrayConditions_AreIgnored_KeepsLineAggregate()
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

        await Assert.That(f.BranchesHit).IsEqualTo(2);     // line-level aggregate (2/4) preserved
        await Assert.That(f.BranchesTotal).IsEqualTo(4);
        await Assert.That(f.ConditionsByLine).IsEmpty();   // stray / non-numeric / coverage-less conditions all dropped
    }

    [Test]
    public async Task Parse_WellFormedXml_EmitsNoWarnings()
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

        await Assert.That(report.Warnings).IsEmpty();
    }

    [Test]
    public async Task Parse_MalformedConditionString_EmitsWarning()
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

        await Assert.That(report.Files[0].BranchesTotal).IsEqualTo(0);
        var w = await Assert.That(report.Warnings).HasSingleItem();
        await Assert.That(w.Kind).IsEqualTo(CoverageWarningKind.MalformedConditionCoverage);
        await Assert.That(w.File).IsEqualTo("src/A.cs");
        await Assert.That(w.Line).IsEqualTo(42);
        await Assert.That(w.Detail).Contains("garbage");
    }

    [Test]
    [Arguments("50% (99999999999999/2)")]
    [Arguments("50% (1/99999999999999)")]
    public async Task Parse_ConditionCoverageWithIntOverflow_EmitsWarning(string condition)
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
        await Assert.That(report.Files[0].BranchesTotal).IsEqualTo(0);
        // ...and the drop is observable as a structured warning.
        var w = await Assert.That(report.Warnings).HasSingleItem();
        await Assert.That(w.Kind).IsEqualTo(CoverageWarningKind.MalformedConditionCoverage);
        await Assert.That(w.Detail).Contains(condition);
    }

    [Test]
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

        var w = await Assert.That(report.Warnings).HasSingleItem();
        await Assert.That(w.Kind).IsEqualTo(CoverageWarningKind.MalformedConditionCoverage);
        await Assert.That(w.Line).IsEqualTo(3);
    }

    [Test]
    public async Task Parse_MultipleClassBlocksSameFile_UnionLinesWithMaxHits()
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

        await Assert.That(file.Path).IsEqualTo("src/Dto.cs");
        await Assert.That(file.LinesTotal).IsEqualTo(3);
        await Assert.That(file.LinesHit).IsEqualTo(2);
        await Assert.That(file.UncoveredLines).IsEquivalentTo([12], CollectionOrdering.Matching);
        await Assert.That(file.LineHits[10]).IsEqualTo(5);
    }

    // ── Path identity: separator normalization and case-insensitivity ──
    // The normalized filename is the file's merge-identity key across Windows/Linux CI jobs;
    // these pin the contract the ConsumeClass comment declares.

    [Test]
    public async Task Parse_BackslashFilename_NormalizesToForwardSlashPath()
    {
        var report = Cobertura.NewDoc()
            .AddClass(@"src\App\A.cs", c => c.Line(1, hits: 1))
            .Parse();

        var file = await Assert.That(report.Files).HasSingleItem();
        await Assert.That(file.Path).IsEqualTo("src/App/A.cs");
    }

    [Test]
    public async Task Merge_BackslashAndForwardSlashReports_UnionAsOneFile()
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

        var file = await Assert.That(merged.Files).HasSingleItem();
        await Assert.That(file.LinesTotal).IsEqualTo(3);
        await Assert.That(file.LinesHit).IsEqualTo(2);   // 1 from windows, 2 from linux
        await Assert.That(file.UncoveredLines).IsEquivalentTo([3], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Parse_ClassBlocksDifferingOnlyInPathCase_StayDistinctFiles()
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

        await Assert.That(report.Files.Count).IsEqualTo(2);
        await Assert.That(report.TotalLines).IsEqualTo(4);
        await Assert.That(report.TotalLinesHit).IsEqualTo(2);
        await Assert.That(report.LineRate).IsEqualTo(0.5);
        var uncoveredFile = report.Files.Single(f => f.Path == "net/netfilter/xt_tcpmss.c");
        await Assert.That(uncoveredFile.LineRate).IsEqualTo(0.0);
    }
}