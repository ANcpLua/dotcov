using System.Text;
using DotCov.Tests.Infrastructure;
using DotCov.Tool;

namespace DotCov.Tests;
/// <summary>
/// Parser rules that keep a number from depending on how the XML is packaged or spelled:
/// condition identity across the class blocks of one document, branch data that contradicts
/// itself, an empty class summary, and redundant <c>.</c> segments in file names.
/// </summary>
[TestClass]
public sealed class ParserIntegrityTests : IDisposable
{
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-integrity-");
    public void Dispose() => _ws.Dispose();
    private static async Task<(int Code, string StdOut, string StdErr)> Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await DotCovCli.RunAsync(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static CoverageReport ParseXml(string xml) => CoberturaParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)));
    // ── A4: condition identity does not depend on XML packaging ───────────────
    private static void BlockWithCondition5(Cobertura.ClassBuilder c) => c.Line(9, hits: 1).BranchWithConditions(10, "50% (1/2)", (5, "50%"));
    private static void BlockWithCondition9(Cobertura.ClassBuilder c) => c.BranchWithConditions(10, "50% (1/2)", (9, "50%"));
    [TestMethod]
    public void ConditionIdentity_OneDocumentWithTwoBlocks_EqualsTwoMergedDocuments()
    {
        // The same measurements, packaged once as two <class> blocks of one document and once
        // as two documents. Different condition numbers on line 10 do not identify the same
        // branches, so neither packaging may union them into a (2/4) the emitter never reported.
        var oneDocument = Cobertura.NewDoc().AddClass("src/X.cs", "Ns.X", BlockWithCondition5).AddClass("src/X.cs", "Ns.X/<Run>d__0", BlockWithCondition9).Parse();
        var twoDocuments = CoverageReport.Merge(Cobertura.NewDoc().AddClass("src/X.cs", "Ns.X", BlockWithCondition5).Parse(), Cobertura.NewDoc().AddClass("src/X.cs", "Ns.X/<Run>d__0", BlockWithCondition9).Parse());
        foreach (var report in new[]
        {
            oneDocument,
            twoDocuments
        }

        )
        {
            var file = report.Files.Single();
            Assert.AreEqual((1, 2), (file.BranchesHit, file.BranchesTotal));
            Assert.AreEqual((2, 2), (file.LinesHit, file.LinesTotal));
            Assert.IsEmpty(file.ConditionsByLine[10]); // poisoned: raw aggregate only
        }

        Assert.AreEqual(twoDocuments.BranchRate, oneDocument.BranchRate);
        Assert.AreSequenceEqual(twoDocuments.Warnings.Select(static w => (w.Kind, w.File, w.Line)), oneDocument.Warnings.Select(static w => (w.Kind, w.File, w.Line)), SequenceOrder.InAnyOrder);
        var warning = Assert.ContainsSingle(oneDocument.Warnings);
        Assert.AreEqual(CoverageWarningKind.ConditionIdentityMismatch, warning.Kind);
        Assert.AreEqual("condition numbers [5] vs [9] - using the line aggregate", warning.Detail);
    }

    [TestMethod]
    public void ConditionIdentity_SameSetInTwoBlocks_UnionsPerNumber_LikeAMerge()
    {
        // Same condition numbers: two blocks that each exercised a different condition raise
        // each other, exactly as two merged documents do.
        static void First(Cobertura.ClassBuilder c) => c.BranchWithConditions(10, "25% (1/4)", (5, "50%"), (7, "0%"));
        static void Second(Cobertura.ClassBuilder c) => c.BranchWithConditions(10, "25% (1/4)", (5, "0%"), (7, "50%"));
        var oneDocument = Cobertura.NewDoc().AddClass("src/X.cs", "Ns.X", First).AddClass("src/X.cs", "Ns.X/<>c", Second).Parse();
        var twoDocuments = CoverageReport.Merge(Cobertura.NewDoc().AddClass("src/X.cs", "Ns.X", First).Parse(), Cobertura.NewDoc().AddClass("src/X.cs", "Ns.X/<>c", Second).Parse());
        Assert.AreEqual((2, 4), oneDocument.Files.Single().BranchesByLine[10]);
        Assert.AreEqual((2, 4), twoDocuments.Files.Single().BranchesByLine[10]);
        Assert.IsEmpty(oneDocument.Warnings);
    }

    [TestMethod]
    public void ConditionIdentity_PoisonedLine_StaysPoisoned_WarningOnce()
    {
        // A third block that agrees with the first cannot resurrect the detail: the poison is
        // absorbing, so the result does not depend on block order.
        var report = Cobertura.NewDoc().AddClass("src/X.cs", "Ns.X", BlockWithCondition5).AddClass("src/X.cs", "Ns.X/<A>d__0", BlockWithCondition9).AddClass("src/X.cs", "Ns.X/<B>d__1", c => c.BranchWithConditions(10, "100% (2/2)", (5, "100%"))).Parse();
        var file = report.Files.Single();
        Assert.AreEqual((2, 2), file.BranchesByLine[10]); // raw Math.Max aggregate
        Assert.IsEmpty(file.ConditionsByLine[10]);
        Assert.AreEqual(1, report.Warnings.Count(static w => w.Kind is CoverageWarningKind.ConditionIdentityMismatch));
    }

    [TestMethod]
    public void ConditionDetail_OnlyFromABlockWithASmallerTotal_FallsBackToTheAggregate()
    {
        // Line 10's larger total came from a block without detail; one condition cannot stand for
        // four outcomes, so the detail is left out — silently, nothing is malformed.
        var report = Cobertura.NewDoc().AddClass("src/X.cs", "Ns.X", c => c.BranchWithConditions(10, "50% (1/2)", (5, "50%"))).AddClass("src/X.cs", "Ns.X/<>c", c => c.Branch(10, "50% (2/4)")).Parse();
        var file = report.Files.Single();
        Assert.AreEqual((2, 4), file.BranchesByLine[10]);
        Assert.IsFalse(file.ConditionsByLine.ContainsKey(10));
        Assert.IsEmpty(report.Warnings);
    }

    [TestMethod]
    public void ConditionIdentity_MethodLinesAndClassLines_RepeatTheSameDetail_WithoutAWarning()
    {
        // Coverlet's own repetition: the same branched line under its <method> and in the class
        // summary carries the same condition set and unions to itself.
        const string xml = """
            <coverage><packages><package><classes>
              <class name="Ns.X" filename="src/X.cs">
                <methods><method name="M" signature="()"><lines>
                  <line number="10" hits="1" branch="True" condition-coverage="50% (1/2)">
                    <conditions><condition number="5" type="jump" coverage="50%"/></conditions>
                  </line>
                </lines></method></methods>
                <lines>
                  <line number="10" hits="1" branch="True" condition-coverage="50% (1/2)">
                    <conditions><condition number="5" type="jump" coverage="50%"/></conditions>
                  </line>
                </lines>
              </class>
            </classes></package></packages></coverage>
            """;
        var report = ParseXml(xml);
        Assert.AreSequenceEqual(new Dictionary<int, int> { [5] = 1 }, report.Files.Single().ConditionsByLine[10], SequenceOrder.InAnyOrder);
        Assert.IsEmpty(report.Warnings);
    }

    [TestMethod]
    public void ConditionIdentity_MethodOnlyClasses_ApplyTheRuleWhenMethodLinesStandIn()
    {
        // Without class-level lines, method lines stand in for the file: two method-only blocks
        // with different condition numbers on one line poison it, like class-level lines do.
        const string xml = """
            <coverage><packages><package><classes>
              <class name="Ns.X" filename="src/X.cs"><methods><method name="M" signature="()"><lines>
                <line number="10" hits="1" branch="True" condition-coverage="50% (1/2)">
                  <conditions><condition number="5" type="jump" coverage="50%"/></conditions>
                </line>
              </lines></method></methods></class>
              <class name="Ns.X/&lt;M&gt;d__0" filename="src/X.cs"><methods><method name="MoveNext" signature="()"><lines>
                <line number="10" hits="1" branch="True" condition-coverage="50% (1/2)">
                  <conditions><condition number="9" type="jump" coverage="50%"/></conditions>
                </line>
              </lines></method></methods></class>
            </classes></package></packages></coverage>
            """;
        var report = ParseXml(xml);
        Assert.AreEqual((1, 2), report.Files.Single().BranchesByLine[10]);
        Assert.AreEqual(CoverageWarningKind.ConditionIdentityMismatch, report.Warnings.Single().Kind);
    }

    // ── A5: branch data that contradicts itself warns and never passes ────────
    [TestMethod]
    public async Task BranchLineWithoutConditionCoverage_WarnsAndCountsNoBranch()
    {
        const string xml = """
            <coverage><packages><package><classes>
              <class name="X" filename="src/X.cs"><lines>
                <line number="1" hits="1" branch="false"/>
                <line number="2" hits="1" branch="true"/>
              </lines></class>
            </classes></package></packages></coverage>
            """;
        var report = ParseXml(xml);
        var warning = Assert.ContainsSingle(report.Warnings);
        Assert.AreEqual(CoverageWarningKind.MalformedConditionCoverage, warning.Kind);
        Assert.AreEqual(("src/X.cs", 2), (warning.File, warning.Line));
        Assert.Contains("without a condition-coverage attribute", warning.Detail);
        Assert.IsFalse(report.HasBranchData);
        Assert.AreEqual(GateOutcome.NoData, report.Evaluate(0, 100).Outcome);
        var path = _ws.Write("no-condition-coverage.cobertura.xml", xml);
        var(code, stdout, stderr) = await Run("check", path, "--min-branch", "100");
        Assert.AreEqual(DotCovCli.NoDataExitCode, code);
        Assert.DoesNotContain("PASS", stdout + stderr);
        Assert.Contains("warning: src/X.cs:2: branch=\"true\" without a condition-coverage attribute", stderr);
    }

    [TestMethod]
    public async Task ConditionDetailClaimingMoreThanItsLine_IsDroppedWithAWarning()
    {
        // (0/2) with one condition at 100%: the overlay used to lift the line to (2/2), a fake
        // 100% branch PASS from a report that says no branch outcome was taken.
        var doc = Cobertura.NewDoc().AddClass("src/X.cs", c => c.BranchWithConditions(4, "0% (0/2)", (11, "100%")));
        var report = doc.Parse();
        var file = report.Files.Single();
        Assert.AreEqual((0, 2), (file.BranchesHit, file.BranchesTotal));
        Assert.IsEmpty(file.ConditionsByLine);
        var warning = Assert.ContainsSingle(report.Warnings);
        Assert.AreEqual(CoverageWarningKind.MalformedConditionCoverage, warning.Kind);
        Assert.AreEqual("1 conditions with 2 covered outcomes disagree with condition-coverage (0/2) - using the line aggregate", warning.Detail);
        Assert.AreEqual(GateOutcome.Fail, report.Evaluate(0, 100).Outcome);
        var(code, stdout, stderr) = await Run("check", _ws.Write("lifted.cobertura.xml", doc), "--min-branch", "100");
        Assert.AreEqual(1, code);
        Assert.DoesNotContain("PASS", stdout + stderr);
    }

    [TestMethod]
    [DataRow("lots", "5")]
    [DataRow("50%", "five")]
    public void UnparseableCondition_IsDroppedWithAWarning(string coverage, string number)
    {
        var report = ParseXml($"""
            <coverage><packages><package><classes>
              <class name="X" filename="src/X.cs"><lines>
                <line number="4" hits="1" branch="true" condition-coverage="50% (1/2)">
                  <conditions><condition number="{number}" type="jump" coverage="{coverage}"/></conditions>
                </line>
              </lines></class>
            </classes></package></packages></coverage>
            """);
        Assert.AreEqual((1, 2), report.Files.Single().BranchesByLine[4]);
        Assert.IsEmpty(report.Files.Single().ConditionsByLine);
        var warning = Assert.ContainsSingle(report.Warnings);
        Assert.AreEqual("a <condition> could not be parsed - using the line aggregate", warning.Detail);
    }

    [TestMethod]
    public void ConditionNumberRepeatedWithinALine_UnionsWithMax()
    {
        var report = Cobertura.NewDoc().AddClass("src/X.cs", c => c.BranchWithConditions(4, "50% (1/2)", (5, "0%"), (5, "50%"), (5, "0%"))).Parse();
        Assert.AreSequenceEqual(new Dictionary<int, int> { [5] = 1 }, report.Files.Single().ConditionsByLine[4], SequenceOrder.InAnyOrder);
        Assert.IsEmpty(report.Warnings);
    }

    [TestMethod]
    public void SwitchCondition_IsNotTwoWayDetail_AndKeepsTheAggregateSilently()
    {
        // Coverlet's switch: one condition for three outcomes. Not malformed, just not 2-way.
        var report = Cobertura.NewDoc().AddClass("src/X.cs", c => c.BranchWithConditions(4, "66% (2/3)", (7, "66.67%"))).Parse();
        Assert.AreEqual((2, 3), report.Files.Single().BranchesByLine[4]);
        Assert.IsEmpty(report.Files.Single().ConditionsByLine);
        Assert.IsEmpty(report.Warnings);
    }

    // ── A6: an empty class summary still decides the file's lines ─────────────
    [TestMethod]
    [DataRow("<lines/>")]
    [DataRow("<lines></lines>")]
    public async Task EmptyClassSummary_KeepsMethodDeclarationLinesOut_FileIsNoData(string emptyLines)
    {
        // istanbul lists each function's declaration line under <methods>; the class's own
        // <lines> — here empty — says the file has no statements, so nothing was measured.
        var xml = $"""
            <coverage><packages><package><classes>
              <class name="index.js" filename="src/index.js">
                <methods><method name="(anonymous_0)" signature="()V"><lines>
                  <line number="1" hits="1"/>
                </lines></method></methods>
                {emptyLines}
              </class>
            </classes></package></packages></coverage>
            """;
        var sync = ParseXml(xml);
        var async = await CoberturaParser.ParseAsync(new MemoryStream(Encoding.UTF8.GetBytes(xml)));
        foreach (var report in new[]
        {
            sync,
            async
        }

        )
        {
            Assert.AreEqual(0, report.Files.Single().LinesTotal);
            Assert.AreEqual(GateOutcome.NoData, report.Evaluate(80).Outcome);
        }

        var(code, stdout, stderr) = await Run("check", _ws.Write("empty-summary.cobertura.xml", xml), "--min-line", "80");
        Assert.AreEqual(DotCovCli.NoDataExitCode, code);
        Assert.DoesNotContain("PASS", stdout + stderr);
        Assert.StartsWith("NODATA:", stderr);
    }

    // ── A7: one file, however its relative name is spelled ───────────────────
    [TestMethod]
    public void DotSegments_InFilenames_FoldIntoOneFile()
    {
        var report = Cobertura.NewDoc().AddClass("./src/C.cs", "C", c => c.Line(1, hits: 1)).AddClass("src/C.cs", "C", c => c.Line(2, hits: 0)).AddClass("src/./C.cs", "C", c => c.Line(3, hits: 1)).AddClass(".//src/C.cs", "C", c => c.Line(4, hits: 1)).AddClass(@".\src\.\C.cs", "C", c => c.Line(5, hits: 1)).Parse();
        var file = Assert.ContainsSingle(report.Files);
        Assert.AreEqual("src/C.cs", file.Path);
        Assert.AreEqual((4, 5), (file.LinesHit, file.LinesTotal));
    }

    [TestMethod]
    public void DotSegments_RootsAndFilenamesFollowOneRule_ParentSegmentsAreKept()
    {
        var report = Cobertura.NewDoc().WithSource("./repo/./src/.").AddClass("./app/C.cs", "C", c => c.Line(1, hits: 1)).AddClass("app/../app/C.cs", "C", c => c.Line(2, hits: 1)).Parse();
        Assert.AreSequenceEqual(["repo/src"], report.SourceRoots, SequenceOrder.InAnyOrder);
        // `..` is never resolved: that needs the file system's view of links, and the disk is not read.
        Assert.AreSequenceEqual(["repo/src/app/C.cs", "repo/src/app/../app/C.cs"], report.Files.Select(static f => f.Path), SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    [DataRow("./.")]
    [DataRow("/.")]
    public void DotSegments_RootOfOnlyDots_IsTheNoOpOrTheFilesystemRoot(string root)
    {
        var report = Cobertura.NewDoc().WithSource(root).AddClass("app/C.cs", "C", c => c.Line(1, hits: 1)).Parse();
        Assert.AreEqual(root is "/." ? "/app/C.cs" : "app/C.cs", report.Files.Single().Path);
    }
}
