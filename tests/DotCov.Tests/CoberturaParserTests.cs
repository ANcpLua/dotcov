using System.Text;
using System.Xml;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;
[TestClass]
public sealed class CoberturaParserTests(TestContext testContext)
{
    private const string FixturePath = "Fixtures/sample.cobertura.xml";
    [TestMethod]
    public void Parse_FullyCoveredClass_ReportsAllLinesHit()
    {
        var report = CoberturaParser.Parse(ReportInput.FromFile(FixturePath));
        var calculator = Assert.ContainsSingle(f => f.Path == "src/Calculator.cs", report.Files);
        Assert.AreEqual(4, calculator.LinesTotal);
        Assert.AreEqual(4, calculator.LinesHit);
        Assert.AreEqual(1.0, calculator.LineRate);
    }

    [TestMethod]
    public void Parse_PartiallyCoveredClass_ReportsCorrectHitCount()
    {
        var report = CoberturaParser.Parse(ReportInput.FromFile(FixturePath));
        var parser = Assert.ContainsSingle(f => f.Path == "src/Parser.cs", report.Files);
        Assert.AreEqual(5, parser.LinesTotal);
        Assert.AreEqual(3, parser.LinesHit);
        Assert.AreEqual(0.6, parser.LineRate);
    }

    [TestMethod]
    public void Parse_UncoveredClass_ReportsZeroLineRate()
    {
        var report = CoberturaParser.Parse(ReportInput.FromFile(FixturePath));
        var unused = Assert.ContainsSingle(f => f.Path == "src/Unused.cs", report.Files);
        Assert.AreEqual(3, unused.LinesTotal);
        Assert.AreEqual(0, unused.LinesHit);
        Assert.AreEqual(0.0, unused.LineRate);
    }

    [TestMethod]
    public void Parse_FullBranchCoverage_ReportsAllBranchesHit()
    {
        var report = CoberturaParser.Parse(ReportInput.FromFile(FixturePath));
        var calculator = Assert.ContainsSingle(f => f.Path == "src/Calculator.cs", report.Files);
        Assert.AreEqual(2, calculator.BranchesTotal);
        Assert.AreEqual(2, calculator.BranchesHit);
        Assert.AreEqual(1.0, calculator.BranchRate);
    }

    [TestMethod]
    public void Parse_PartialBranches_ExtractsConditionCoverageCorrectly()
    {
        var report = CoberturaParser.Parse(ReportInput.FromFile(FixturePath));
        var parser = Assert.ContainsSingle(f => f.Path == "src/Parser.cs", report.Files);
        Assert.AreEqual(4, parser.BranchesTotal);
        Assert.AreEqual(1, parser.BranchesHit);
        Assert.AreEqual(0.25, parser.BranchRate);
    }

    [TestMethod]
    public void Parse_NoBranches_ReportsNoBranchRate()
    {
        var report = CoberturaParser.Parse(ReportInput.FromFile(FixturePath));
        var unused = Assert.ContainsSingle(f => f.Path == "src/Unused.cs", report.Files);
        Assert.AreEqual(0, unused.BranchesTotal);
        // Not 1.0. A file with no branches has no branch rate - reporting "100%" for absent
        // data would let a --min-branch gate pass on reports carrying no branch data at all.
        Assert.IsNull(unused.BranchRate);
        Assert.IsFalse(unused.HasBranchData);
    }

    [TestMethod]
    public void Report_AggregateTotals_SumsAcrossAllFiles()
    {
        var report = CoberturaParser.Parse(ReportInput.FromFile(FixturePath));
        Assert.AreEqual(12, report.TotalLines);
        Assert.AreEqual(7, report.TotalLinesHit);
        Assert.AreEqual(6, report.TotalBranches);
        Assert.AreEqual(3, report.TotalBranchesHit);
    }

    [TestMethod]
    public void Evaluate_AboveMinimum_Passes()
    {
        var report = CoberturaParser.Parse(ReportInput.FromFile(FixturePath));
        Assert.AreEqual(GateOutcome.Pass, report.Evaluate(50).Outcome);
    }

    [TestMethod]
    public void Evaluate_BelowMinimum_Fails()
    {
        var report = CoberturaParser.Parse(ReportInput.FromFile(FixturePath));
        var gate = report.Evaluate(80);
        Assert.AreEqual(GateOutcome.Fail, gate.Outcome);
        Assert.Contains("line coverage below threshold", gate.Reason);
    }

    [TestMethod]
    public void Evaluate_WithBranchMinimum_ChecksBoth()
    {
        var report = CoberturaParser.Parse(ReportInput.FromFile(FixturePath));
        Assert.AreEqual(GateOutcome.Pass, report.Evaluate(50, 50).Outcome);
        Assert.AreEqual(GateOutcome.Fail, report.Evaluate(50, 60).Outcome);
    }

    [TestMethod]
    public void Offenders_ReturnOnlyFilesUnderTheFailedThreshold_WorstFirst()
    {
        var report = CoberturaParser.Parse(ReportInput.FromFile(FixturePath));
        var offenders = report.Offenders(report.Evaluate(80));
        Assert.AreSequenceEqual(["src/Unused.cs", "src/Parser.cs"], offenders.Select(static f => f.Path), SequenceOrder.InOrder);
    }

    [TestMethod]
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
        // The assertion executes and completes within the captured resource lifetime.
        // ReSharper disable once AccessToDisposedClosure
        Assert.ThrowsExactly<XmlException>(() => CoberturaParser.Parse(stream));
    }

    [TestMethod]
    public async Task Parse_BenignDoctype_ParsesLikeReferenceCobertura()
    {
        // Reference Cobertura, gcovr, and coverage.py emit this DOCTYPE on every report.
        // DtdProcessing.Prohibit would reject the format's canonical emitters; the DOCTYPE must
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
        var report = await CoberturaParser.ParseAsync(stream, ct: testContext.CancellationToken);
        var file = Assert.ContainsSingle(report.Files);
        Assert.AreEqual(2, file.LinesTotal);
        Assert.AreEqual(1, file.LinesHit);
        Assert.IsEmpty(report.Warnings);
    }

    [TestMethod]
    public async Task Parse_EmptyPackages_ReturnsEmptyReport()
    {
        const string xml = """
                           <?xml version="1.0"?>
                           <coverage><packages></packages></coverage>
                           """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var report = await CoberturaParser.ParseAsync(stream, ct: testContext.CancellationToken);
        Assert.IsEmpty(report.Files);
        Assert.IsNull(report.LineRate);
        Assert.IsFalse(report.HasLineData);
    }

    [TestMethod]
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
        var report = await CoberturaParser.ParseAsync(stream, ct: testContext.CancellationToken);
        var file = Assert.ContainsSingle(report.Files);
        Assert.AreEqual(4, file.LinesTotal);
        Assert.AreEqual(2, file.LinesHit);
        Assert.AreEqual(4, file.BranchesTotal);
        Assert.AreEqual(1, file.BranchesHit);
        Assert.AreSequenceEqual([20, 21], file.UncoveredLines, SequenceOrder.InOrder);
    }

    [TestMethod]
    public void Parse_IstanbulLayout_MethodDeclarationLinesAreNotFileLines()
    {
        // istanbul (Jest, Vitest) lists each function's declaration line under <methods>; the
        // class-level <lines> are its line coverage and need not contain that line.
        const string xml = """
                           <?xml version="1.0"?>
                           <coverage><packages><package><classes>
                             <class name="x.ts" filename="x.ts">
                               <methods>
                                 <method name="run" hits="2" signature="()V">
                                   <lines><line number="3" hits="2" /></lines>
                                 </method>
                               </methods>
                               <lines>
                                 <line number="4" hits="2" branch="false" />
                                 <line number="5" hits="0" branch="true" condition-coverage="50% (1/2)" />
                               </lines>
                             </class>
                           </classes></package></packages></coverage>
                           """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        // Exercise the synchronous parser contract alongside awaited assertions.
        // ReSharper disable once MethodHasAsyncOverload
        var file = Assert.ContainsSingle(CoberturaParser.Parse(stream).Files);
        Assert.AreEqual(2, file.LinesTotal);
        Assert.AreEqual(1, file.LinesHit);
        Assert.AreSequenceEqual([5], file.UncoveredLines, SequenceOrder.InOrder);
        Assert.AreEqual(1, file.BranchesHit);
        Assert.AreEqual(2, file.BranchesTotal);
    }

    [TestMethod]
    public void Parse_ClassWithoutClassLevelLines_CountsItsMethodLines()
    {
        const string xml = """
                           <?xml version="1.0"?>
                           <coverage><packages><package><classes>
                             <class name="X" filename="x.cs">
                               <methods>
                                 <method name="A" signature="()">
                                   <lines>
                                     <line number="10" hits="1" branch="False" />
                                     <line number="11" hits="1" branch="True" condition-coverage="50% (1/2)">
                                       <conditions><condition number="0" type="jump" coverage="50%" /></conditions>
                                     </line>
                                   </lines>
                                 </method>
                               </methods>
                             </class>
                           </classes></package></packages></coverage>
                           """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        // Exercise the synchronous parser contract alongside awaited assertions.
        // ReSharper disable once MethodHasAsyncOverload
        var file = Assert.ContainsSingle(CoberturaParser.Parse(stream).Files);
        Assert.AreEqual(2, file.LinesTotal);
        Assert.AreEqual(2, file.LinesHit);
        Assert.AreEqual((1, 2), file.BranchesByLine[11]);
        Assert.AreEqual(1, file.ConditionsByLine[11][0]);
    }

    [TestMethod]
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
        var file = Assert.ContainsSingle((await CoberturaParser.ParseAsync(stream, ct: testContext.CancellationToken)).Files);
        Assert.HasCount(2, file.BranchesByLine);
        Assert.AreEqual((1, 2), file.BranchesByLine[10]);
        Assert.AreEqual((4, 4), file.BranchesByLine[20]);
        Assert.DoesNotContain(30, file.BranchesByLine.Keys);
        Assert.AreEqual(LineStatus.Partial, file.GetLineStatus(10));
        Assert.AreEqual(LineStatus.Hit, file.GetLineStatus(20));
        Assert.AreEqual(LineStatus.Hit, file.GetLineStatus(30));
    }

    [TestMethod]
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
        var report = await CoberturaParser.ParseAsync(stream, ct: testContext.CancellationToken);
        var file = Assert.ContainsSingle(report.Files);
        Assert.AreEqual(1, file.LinesTotal);
        Assert.AreEqual(0, file.LinesHit);
        Assert.AreSequenceEqual([1], file.UncoveredLines, SequenceOrder.InOrder);
        Assert.IsEmpty(report.Warnings);
    }

    [TestMethod]
    [DataRow("not-a-number")]
    [DataRow("1.5")]
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
        var report = await CoberturaParser.ParseAsync(stream, ct: testContext.CancellationToken);
        var file = Assert.ContainsSingle(report.Files);
        Assert.AreEqual(1, file.LinesTotal);
        Assert.AreEqual(0, file.LinesHit);
        var w = Assert.ContainsSingle(report.Warnings);
        Assert.AreEqual(CoverageWarningKind.MalformedHits, w.Kind);
        Assert.AreEqual("src/A.cs", w.File);
        Assert.AreEqual(7, w.Line);
        Assert.Contains(hits, w.Detail);
    }

    [TestMethod]
    public void Parse_MalformedLineUnderMethodAndClass_WarnsOncePerAggregation()
    {
        // Coverlet repeats every line under its <method> and in the class summary; the one
        // malformed source line warns once per aggregation, not once per repetition.
        var document = Cobertura.NewDoc().AddClass("src/A.cs", "MyApp.A", c => c.Method("M", "()", "1", m => m.MalformedLine("7", "lots")).MalformedLine("7", "lots"));
        var fileWarning = Assert.ContainsSingle(document.Parse().Warnings);
        Assert.AreEqual(7, fileWarning.Line);
        using var stream = document.ToStream();
        Assert.ContainsSingle(CoberturaParser.ParseMethods(stream).Warnings);
    }

    [TestMethod]
    public async Task Parse_HitsAboveIntMax_CountsAsCoveredLine()
    {
        // 64-bit hit counts are real (soak runs; gcovr/llvm-cov/JaCoCo converters use long
        // counters and the Cobertura DTD does not bound hits). Overflow must saturate, not
        // degrade to 0 — degrading would silently flip a covered line to a miss and deflate
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
        var report = await CoberturaParser.ParseAsync(stream, ct: testContext.CancellationToken);
        var file = Assert.ContainsSingle(report.Files);
        Assert.AreEqual(2, file.LinesTotal);
        Assert.AreEqual(2, file.LinesHit);
        Assert.AreEqual(1.0, file.LineRate);
        Assert.AreEqual(int.MaxValue, file.LineHits[1]); // saturated, still "covered"
        Assert.IsEmpty(report.Warnings);
    }

    [TestMethod]
    [DataRow("true")]
    [DataRow("True")]
    [DataRow("TRUE")]
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
        var report = await CoberturaParser.ParseAsync(stream, ct: testContext.CancellationToken);
        Assert.AreEqual(2, report.TotalBranches);
        Assert.AreEqual(1, report.TotalBranchesHit);
    }

    [TestMethod]
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
        var report = await CoberturaParser.ParseAsync(stream, ct: testContext.CancellationToken);
        Assert.ContainsSingle(report.Files);
        Assert.AreEqual(0, report.Files[0].LinesTotal);
        // A class the emitter listed but recorded no lines for is unmeasured, not fully covered.
        Assert.IsNull(report.Files[0].LineRate);
    }

    [TestMethod]
    public void Merge_TwoReports_CombinesFilesByPath()
    {
        var a = new CoverageReport([new FileCoverage("a.cs", 3, 10, 0, 0)]);
        var b = new CoverageReport([new FileCoverage("b.cs", 5, 10, 0, 0)]);
        var merged = CoverageReport.Merge(a, b);
        Assert.HasCount(2, merged.Files);
    }

    [TestMethod]
    public void Merge_SameFile_DedupesLinesByNumberTakingMaxHits()
    {
        var a = Reports.ClassifiedFile("a.cs", linesHit: 2, linesTotal: 4, branchesHit: 1, branchesTotal: 2, lineHits: new Dictionary<int, int> { [1] = 3, [2] = 0, [3] = 5, [4] = 0 }, branchesByLine: new Dictionary<int, (int Covered, int Total)> { [1] = (1, 2) });
        var b = Reports.ClassifiedFile("a.cs", linesHit: 3, linesTotal: 4, branchesHit: 2, branchesTotal: 4, lineHits: new Dictionary<int, int> { [1] = 1, [2] = 4, [3] = 2, [5] = 7 }, branchesByLine: new Dictionary<int, (int Covered, int Total)> { [3] = (1, 2), [5] = (1, 2) });
        var merged = CoverageReport.Merge(new CoverageReport([a]), new CoverageReport([b]));
        Assert.ContainsSingle(merged.Files);
        Assert.AreEqual(5, merged.Files[0].LinesTotal);
        Assert.AreEqual(4, merged.Files[0].LinesHit);
        Assert.AreEqual(3, merged.Files[0].BranchesHit);
        Assert.AreEqual(6, merged.Files[0].BranchesTotal);
    }

    [TestMethod]
    public void Merge_SameFile_OverlappingBranchLines_DedupesViaMathMax()
    {
        var a = Reports.ClassifiedFile("a.cs", 1, 1, 1, 2, lineHits: new Dictionary<int, int> { [10] = 1 }, branchesByLine: new Dictionary<int, (int Covered, int Total)> { [10] = (1, 2) });
        var b = Reports.ClassifiedFile("a.cs", 1, 1, 2, 2, lineHits: new Dictionary<int, int> { [10] = 5 }, branchesByLine: new Dictionary<int, (int Covered, int Total)> { [10] = (2, 2) });
        var(merged, _) = a.MergeWith(b);
        Assert.AreEqual(1, merged.LinesTotal);
        Assert.AreEqual(2, merged.BranchesHit);
        Assert.AreEqual(2, merged.BranchesTotal);
    }

    [TestMethod]
    public void Merge_SplitConditionRuns_UnionsByConditionNumber_NotLineLevelMax()
    {
        // Two runs cover DIFFERENT conditions of the same branched line. Coverlet exposes
        // per-branch identity (<condition number= coverage=>), so the true union is 5/6 — a
        // line-level Math.Max on the (3/6) counts wrongly reports 3/6 (the false not-hit).
        var a = Cobertura.NewDoc().AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "50% (3/6)", (1, "100%"), (2, "50%"), (3, "0%"))).Parse();
        var b = Cobertura.NewDoc().AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "50% (3/6)", (1, "0%"), (2, "50%"), (3, "100%"))).Parse();
        var merged = CoverageReport.Merge(a, b);
        Assert.AreEqual(5, merged.TotalBranchesHit);
        Assert.AreEqual(6, merged.TotalBranches);
    }

    [TestMethod]
    public void Parse_UnparseableConditionCoverage_IgnoresConditionKeepsLineAggregate()
    {
        // A coverlet emitter regression (garbage `coverage`) must not crash and must not poison
        // the per-condition map — the line still parses via its line-level aggregate.
        var f = Cobertura.NewDoc().AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "50% (1/2)", (1, "garbage"))).Parse().Files[0];
        Assert.AreEqual(1, f.BranchesHit); // aggregate (1/2) preserved
        Assert.AreEqual(2, f.BranchesTotal);
        Assert.IsEmpty(f.ConditionsByLine); // the unparseable condition was dropped
    }

    [TestMethod]
    public void Parse_ConditionCountInconsistentWithAggregate_DropsConditionDetail()
    {
        // 1 condition but the line aggregate reports 4 outcomes (a switch jump-table). 1*2 != 4,
        // so the 2-outcome reconstruction is unsafe — drop to the aggregate rather than invent a total.
        var f = Cobertura.NewDoc().AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "25% (1/4)", (1, "50%"))).Parse().Files[0];
        Assert.AreEqual(4, f.BranchesTotal); // aggregate (1/4) preserved
        Assert.IsEmpty(f.ConditionsByLine); // gate dropped the inconsistent detail
    }

    [TestMethod]
    public void Parse_SameConditionUnderMultipleClassBlocks_DedupesViaMathMax()
    {
        // Coverlet emits the same line under <method><lines> AND <class><lines>; the per-condition
        // covered count must Math.Max across blocks, not sum (which would over-count the branch).
        var f = Cobertura.NewDoc().AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "50% (1/2)", (1, "50%"))).AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "100% (2/2)", (1, "100%"))).Parse().Files[0];
        Assert.AreEqual(2, f.ConditionsByLine[10][1]); // Math.Max(1, 2), not 1 + 2
    }

    [TestMethod]
    public void Merge_MismatchedConditionNumberSets_FallsBackToLineAggregateAndWarns()
    {
        // Coverlet condition `number`s are IL branch offsets — stable only for the identical
        // assembly build. When the two sides' number sets for a line differ, they do not
        // identify the same branches, and unioning them would invent a branch total neither
        // emitter reported. The merge must fall back to the line-level Math.Max and warn.
        var a = Cobertura.NewDoc().AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "50% (2/4)", (1, "100%"), (2, "0%"))).Parse();
        var b = Cobertura.NewDoc().AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "66.66% (4/6)", (1, "0%"), (2, "100%"), (3, "100%"))).Parse();
        var merged = CoverageReport.Merge(a, b);
        var f = merged.Files[0];
        Assert.AreEqual(4, f.BranchesHit); // line-level Math.Max of (2/4) and (4/6)
        Assert.AreEqual(6, f.BranchesTotal);
        // The untrustworthy identity is not merely dropped but POISONED: the empty sentinel
        // entry marks the line so no later merge can resurrect one side's detail and make
        // the aggregate depend on fold order (see MergeConditionIdentityTests).
        Assert.IsEmpty(f.ConditionsByLine[10]);
        Assert.Contains(w => w.Kind is CoverageWarningKind.ConditionIdentityMismatch && w.Line == 10, merged.Warnings);
    }

    [TestMethod]
    public void Merge_DisjointConditionNumbersSameTotal_DoesNotInventBranchTotal()
    {
        // Debug vs. Release: the same physical 2-way branch gets condition number 0 in one
        // build and 139 in the other, with identical totals. A per-number union would produce
        // branchesHit=2 of branchesTotal=4 — a total NEITHER emitter reported — with no warning
        // (totals match, so BranchTotalMismatch does not fire). The true union is 2/2.
        var a = Cobertura.NewDoc().AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "100% (2/2)", (0, "100%"))).Parse();
        var b = Cobertura.NewDoc().AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "0% (0/2)", (139, "0%"))).Parse();
        var merged = CoverageReport.Merge(a, b);
        var f = merged.Files[0];
        Assert.AreEqual(2, f.BranchesHit);
        Assert.AreEqual(2, f.BranchesTotal);
        Assert.Contains(w => w.Kind is CoverageWarningKind.ConditionIdentityMismatch && w.Line == 10, merged.Warnings);
    }

    [TestMethod]
    public void Merge_OneSidedConditionDetail_SurvivesAndNeverRegressesTheAggregate()
    {
        // Report `a` carries per-condition detail; `b` (a different emitter) ships only the
        // line aggregate. The detail must be carried forward — an intersection would erase it,
        // silently downgrading every later merge to line-level Math.Max — and the aggregate
        // must keep the higher line-level count `b` observed.
        var a = Cobertura.NewDoc().AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "25% (1/4)", (1, "50%"), (2, "0%"))).Parse();
        var b = Cobertura.NewDoc().AddClass("src/Foo.cs", c => c.Branch(10, "50% (2/4)")).Parse();
        var f = CoverageReport.Merge(a, b).Files[0];
        Assert.Contains(10, f.ConditionsByLine.Keys); // detail survives for future merges
        Assert.AreEqual(1, f.ConditionsByLine[10][1]);
        Assert.AreEqual(0, f.ConditionsByLine[10][2]);
        Assert.AreEqual(2, f.BranchesHit); // b's line-level (2/4) not regressed
        Assert.AreEqual(4, f.BranchesTotal);
    }

    [TestMethod]
    public void Merge_ThreeReports_MiddleWithoutConditionDetail_IsOrderIndependent()
    {
        // Merge(Merge(A,B),C) must equal Merge(A,Merge(B,C)) even when B carries no condition
        // detail: A and C each exercised a DIFFERENT condition of line 10, so the true union
        // is 3/4 — reachable in every merge order only because one-sided detail is carried
        // forward instead of intersected away.
        CoverageReport A() => Cobertura.NewDoc().AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "25% (1/4)", (1, "50%"), (2, "0%"))).Parse();
        CoverageReport B() => Cobertura.NewDoc().AddClass("src/Foo.cs", c => c.Branch(10, "25% (1/4)")).Parse();
        CoverageReport C() => Cobertura.NewDoc().AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "50% (2/4)", (1, "0%"), (2, "100%"))).Parse();
        var leftFold = CoverageReport.Merge(CoverageReport.Merge(A(), B()), C()).Files[0];
        var rightFold = CoverageReport.Merge(A(), CoverageReport.Merge(B(), C())).Files[0];
        Assert.AreEqual(3, leftFold.BranchesHit); // #1 max(1,0)=1, #2 max(0,2)=2
        Assert.AreEqual(4, leftFold.BranchesTotal);
        Assert.AreEqual(leftFold.BranchesHit, rightFold.BranchesHit);
        Assert.AreEqual(leftFold.BranchesTotal, rightFold.BranchesTotal);
    }

    [TestMethod]
    [DataRow("200%")]
    [DataRow("-50%")]
    [DataRow("NaN%")]
    public void Parse_OutOfRangeConditionCoverage_DropsConditionKeepsLineAggregate(string coverage)
    {
        // A condition percent outside [0,100] cannot describe a 2-way jump. Left unclamped,
        // coverage="200%" would record covered=4 for one condition, and a later merge recompute
        // would report BranchesHit=4 of BranchesTotal=2 — a >100% branch rate. The bogus
        // condition is dropped; the line-level aggregate stays authoritative.
        var report = Cobertura.NewDoc().AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "100% (2/2)", (1, coverage))).Parse();
        var f = report.Files[0];
        Assert.AreEqual(2, f.BranchesHit);
        Assert.AreEqual(2, f.BranchesTotal);
        Assert.IsEmpty(f.ConditionsByLine);
        // And the invariant the clamp protects: merging two such reports can never push
        // BranchesHit past BranchesTotal.
        var again = Cobertura.NewDoc().AddClass("src/Foo.cs", c => c.BranchWithConditions(10, "100% (2/2)", (1, coverage))).Parse();
        var merged = CoverageReport.Merge(report, again).Files[0];
        Assert.IsLessThanOrEqualTo(merged.BranchesTotal, merged.BranchesHit);
        Assert.AreEqual(2, merged.BranchesHit);
        Assert.AreEqual(2, merged.BranchesTotal);
    }

    [TestMethod]
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
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var report = await CoberturaParser.ParseAsync(stream, ct: testContext.CancellationToken);
        var f = Assert.ContainsSingle(report.Files);
        Assert.AreEqual(2, f.BranchesHit); // line-level aggregate (2/4) preserved
        Assert.AreEqual(4, f.BranchesTotal);
        Assert.IsEmpty(f.ConditionsByLine); // stray / non-numeric / coverage-less conditions all dropped
    }

    [TestMethod]
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
        var report = await CoberturaParser.ParseAsync(stream, ct: testContext.CancellationToken);
        Assert.IsEmpty(report.Warnings);
    }

    [TestMethod]
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
        var report = await CoberturaParser.ParseAsync(stream, ct: testContext.CancellationToken);
        Assert.AreEqual(0, report.Files[0].BranchesTotal);
        var w = Assert.ContainsSingle(report.Warnings);
        Assert.AreEqual(CoverageWarningKind.MalformedConditionCoverage, w.Kind);
        Assert.AreEqual("src/A.cs", w.File);
        Assert.AreEqual(42, w.Line);
        Assert.Contains("garbage", w.Detail);
    }

    [TestMethod]
    [DataRow("50% (99999999999999/2)")]
    [DataRow("50% (1/99999999999999)")]
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
        var report = await CoberturaParser.ParseAsync(stream, ct: testContext.CancellationToken);
        // The overflowing branch entry is dropped (not silently zeroed INTO the totals)...
        Assert.AreEqual(0, report.Files[0].BranchesTotal);
        // ...and the drop is observable as a structured warning.
        var w = Assert.ContainsSingle(report.Warnings);
        Assert.AreEqual(CoverageWarningKind.MalformedConditionCoverage, w.Kind);
        Assert.Contains(condition, w.Detail);
    }

    [TestMethod]
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
        var report = await CoberturaParser.ParseAsync(stream, ct: testContext.CancellationToken);
        var w = Assert.ContainsSingle(report.Warnings);
        Assert.AreEqual(CoverageWarningKind.MalformedConditionCoverage, w.Kind);
        Assert.AreEqual(3, w.Line);
    }

    [TestMethod]
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
        var file = Assert.ContainsSingle((await CoberturaParser.ParseAsync(stream, ct: testContext.CancellationToken)).Files);
        Assert.AreEqual("src/Dto.cs", file.Path);
        Assert.AreEqual(3, file.LinesTotal);
        Assert.AreEqual(2, file.LinesHit);
        Assert.AreSequenceEqual([12], file.UncoveredLines, SequenceOrder.InOrder);
        Assert.AreEqual(5, file.LineHits[10]);
    }

    // ── Path identity: separator normalization and case-sensitivity ──
    // The normalized filename is the file's merge-identity key across Windows/Linux CI jobs;
    // these pin the contract DecodeFileName declares.
    [TestMethod]
    public void Parse_BackslashFilename_NormalizesToForwardSlashPath()
    {
        var report = Cobertura.NewDoc().AddClass(@"src\App\A.cs", c => c.Line(1, hits: 1)).Parse();
        var file = Assert.ContainsSingle(report.Files);
        Assert.AreEqual("src/App/A.cs", file.Path);
    }

    [TestMethod]
    public void Merge_BackslashAndForwardSlashReports_UnionAsOneFile()
    {
        // The exact Windows+Linux CI matrix scenario: coverlet on Windows writes `src\A.cs`,
        // on Linux `src/A.cs`. The merged report must union their lines as one file, not
        // count the same source file twice.
        var windows = Cobertura.NewDoc().AddClass(@"src\A.cs", c => c.Line(1, hits: 1).Line(2, hits: 0)).Parse();
        var linux = Cobertura.NewDoc().AddClass("src/A.cs", c => c.Line(2, hits: 3).Line(3, hits: 0)).Parse();
        var merged = CoverageReport.Merge(windows, linux);
        var file = Assert.ContainsSingle(merged.Files);
        Assert.AreEqual(3, file.LinesTotal);
        Assert.AreEqual(2, file.LinesHit); // 1 from windows, 2 from linux
        Assert.AreSequenceEqual([3], file.UncoveredLines, SequenceOrder.InOrder);
    }

    [TestMethod]
    public void Parse_ClassBlocksDifferingOnlyInPathCase_StayDistinctFiles()
    {
        // File identity is Ordinal: case-differing filenames are genuinely distinct files on
        // the case-sensitive filesystems Cobertura's native emitters run on —
        // linux/net/netfilter really contains both xt_TCPMSS.c and xt_tcpmss.c.
        // OrdinalIgnoreCase keying would fuse such pairs via Math.Max, silently erasing the
        // fully-uncovered file's misses and reporting 100% where the truth is 50%.
        var report = Cobertura.NewDoc().AddClass("net/netfilter/xt_TCPMSS.c", c => c.Line(1, hits: 1).Line(2, hits: 1)).AddClass("net/netfilter/xt_tcpmss.c", c => c.Line(1, hits: 0).Line(2, hits: 0)).Parse();
        Assert.HasCount(2, report.Files);
        Assert.AreEqual(4, report.TotalLines);
        Assert.AreEqual(2, report.TotalLinesHit);
        Assert.AreEqual(0.5, report.LineRate);
        var uncoveredFile = Assert.ContainsSingle(f => f.Path == "net/netfilter/xt_tcpmss.c", report.Files);
        Assert.AreEqual(0.0, uncoveredFile.LineRate);
    }
}
