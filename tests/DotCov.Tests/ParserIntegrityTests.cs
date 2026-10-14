using System.Text;
using DotCov.Tests.Infrastructure;
using DotCov.Tool;

namespace DotCov.Tests;

/// <summary>
/// Parser rules that keep a number from depending on how the XML is packaged or spelled:
/// condition identity across the class blocks of one document, branch data that contradicts
/// itself, an empty class summary, and redundant <c>.</c> segments in file names.
/// </summary>
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

    private static CoverageReport ParseXml(string xml) =>
        CoberturaParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

    // ── A4: condition identity does not depend on XML packaging ───────────────

    private static void BlockWithCondition5(Cobertura.ClassBuilder c) =>
        c.Line(9, hits: 1).BranchWithConditions(10, "50% (1/2)", (5, "50%"));

    private static void BlockWithCondition9(Cobertura.ClassBuilder c) =>
        c.BranchWithConditions(10, "50% (1/2)", (9, "50%"));

    [Test]
    public async Task ConditionIdentity_OneDocumentWithTwoBlocks_EqualsTwoMergedDocuments()
    {
        // The same measurements, packaged once as two <class> blocks of one document and once
        // as two documents. Different condition numbers on line 10 do not identify the same
        // branches, so neither packaging may union them into a (2/4) the emitter never reported.
        var oneDocument = Cobertura.NewDoc()
            .AddClass("src/X.cs", "Ns.X", BlockWithCondition5)
            .AddClass("src/X.cs", "Ns.X/<Run>d__0", BlockWithCondition9)
            .Parse();
        var twoDocuments = CoverageReport.Merge(
            Cobertura.NewDoc().AddClass("src/X.cs", "Ns.X", BlockWithCondition5).Parse(),
            Cobertura.NewDoc().AddClass("src/X.cs", "Ns.X/<Run>d__0", BlockWithCondition9).Parse());

        foreach (var report in new[] { oneDocument, twoDocuments })
        {
            var file = report.Files.Single();
            await Assert.That((file.BranchesHit, file.BranchesTotal)).IsEqualTo((1, 2));
            await Assert.That((file.LinesHit, file.LinesTotal)).IsEqualTo((2, 2));
            await Assert.That(file.ConditionsByLine[10]).IsEmpty();   // poisoned: raw aggregate only
        }

        await Assert.That(oneDocument.BranchRate).IsEqualTo(twoDocuments.BranchRate);
        await Assert.That(oneDocument.Warnings.Select(static w => (w.Kind, w.File, w.Line)))
            .IsEquivalentTo(twoDocuments.Warnings.Select(static w => (w.Kind, w.File, w.Line)));
        var warning = await Assert.That(oneDocument.Warnings).HasSingleItem();
        await Assert.That(warning.Kind).IsEqualTo(CoverageWarningKind.ConditionIdentityMismatch);
        await Assert.That(warning.Detail).IsEqualTo("condition numbers [5] vs [9] - using the line aggregate");
    }

    [Test]
    public async Task ConditionIdentity_SameSetInTwoBlocks_UnionsPerNumber_LikeAMerge()
    {
        // Same condition numbers: two blocks that each exercised a different condition raise
        // each other, exactly as two merged documents do.
        static void First(Cobertura.ClassBuilder c) => c.BranchWithConditions(10, "25% (1/4)", (5, "50%"), (7, "0%"));
        static void Second(Cobertura.ClassBuilder c) => c.BranchWithConditions(10, "25% (1/4)", (5, "0%"), (7, "50%"));

        var oneDocument = Cobertura.NewDoc()
            .AddClass("src/X.cs", "Ns.X", First).AddClass("src/X.cs", "Ns.X/<>c", Second).Parse();
        var twoDocuments = CoverageReport.Merge(
            Cobertura.NewDoc().AddClass("src/X.cs", "Ns.X", First).Parse(),
            Cobertura.NewDoc().AddClass("src/X.cs", "Ns.X/<>c", Second).Parse());

        await Assert.That(oneDocument.Files.Single().BranchesByLine[10]).IsEqualTo((2, 4));
        await Assert.That(twoDocuments.Files.Single().BranchesByLine[10]).IsEqualTo((2, 4));
        await Assert.That(oneDocument.Warnings).IsEmpty();
    }

    [Test]
    public async Task ConditionIdentity_PoisonedLine_StaysPoisoned_WarningOnce()
    {
        // A third block that agrees with the first cannot resurrect the detail: the poison is
        // absorbing, so the result does not depend on block order.
        var report = Cobertura.NewDoc()
            .AddClass("src/X.cs", "Ns.X", BlockWithCondition5)
            .AddClass("src/X.cs", "Ns.X/<A>d__0", BlockWithCondition9)
            .AddClass("src/X.cs", "Ns.X/<B>d__1", c => c.BranchWithConditions(10, "100% (2/2)", (5, "100%")))
            .Parse();

        var file = report.Files.Single();
        await Assert.That(file.BranchesByLine[10]).IsEqualTo((2, 2));   // raw Math.Max aggregate
        await Assert.That(file.ConditionsByLine[10]).IsEmpty();
        await Assert.That(report.Warnings.Count(static w => w.Kind is CoverageWarningKind.ConditionIdentityMismatch)).IsEqualTo(1);
    }

    [Test]
    public async Task ConditionDetail_OnlyFromABlockWithASmallerTotal_FallsBackToTheAggregate()
    {
        // Line 10's larger total came from a block without detail; one condition cannot stand for
        // four outcomes, so the detail is left out — silently, nothing is malformed.
        var report = Cobertura.NewDoc()
            .AddClass("src/X.cs", "Ns.X", c => c.BranchWithConditions(10, "50% (1/2)", (5, "50%")))
            .AddClass("src/X.cs", "Ns.X/<>c", c => c.Branch(10, "50% (2/4)"))
            .Parse();

        var file = report.Files.Single();
        await Assert.That(file.BranchesByLine[10]).IsEqualTo((2, 4));
        await Assert.That(file.ConditionsByLine.ContainsKey(10)).IsFalse();
        await Assert.That(report.Warnings).IsEmpty();
    }

    [Test]
    public async Task ConditionIdentity_MethodLinesAndClassLines_RepeatTheSameDetail_WithoutAWarning()
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

        await Assert.That(report.Files.Single().ConditionsByLine[10]).IsEquivalentTo(new Dictionary<int, int> { [5] = 1 });
        await Assert.That(report.Warnings).IsEmpty();
    }

    [Test]
    public async Task ConditionIdentity_MethodOnlyClasses_ApplyTheRuleWhenMethodLinesStandIn()
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

        await Assert.That(report.Files.Single().BranchesByLine[10]).IsEqualTo((1, 2));
        await Assert.That(report.Warnings.Single().Kind).IsEqualTo(CoverageWarningKind.ConditionIdentityMismatch);
    }

    // ── A5: branch data that contradicts itself warns and never passes ────────

    [Test]
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

        var warning = await Assert.That(report.Warnings).HasSingleItem();
        await Assert.That(warning.Kind).IsEqualTo(CoverageWarningKind.MalformedConditionCoverage);
        await Assert.That((warning.File, warning.Line)).IsEqualTo(("src/X.cs", 2));
        await Assert.That(warning.Detail).Contains("without a condition-coverage attribute");
        await Assert.That(report.HasBranchData).IsFalse();
        await Assert.That(report.Evaluate(0, 100).Outcome).IsEqualTo(GateOutcome.NoData);

        var path = _ws.Write("no-condition-coverage.cobertura.xml", xml);
        var (code, stdout, stderr) = await Run("check", path, "--min-branch", "100");
        await Assert.That(code).IsEqualTo(DotCovCli.NoDataExitCode);
        await Assert.That(stdout + stderr).DoesNotContain("PASS");
        await Assert.That(stderr).Contains("warning: src/X.cs:2: branch=\"true\" without a condition-coverage attribute");
    }

    [Test]
    public async Task ConditionDetailClaimingMoreThanItsLine_IsDroppedWithAWarning()
    {
        // (0/2) with one condition at 100%: the overlay used to lift the line to (2/2), a fake
        // 100% branch PASS from a report that says no branch outcome was taken.
        var doc = Cobertura.NewDoc()
            .AddClass("src/X.cs", c => c.BranchWithConditions(4, "0% (0/2)", (11, "100%")));

        var report = doc.Parse();

        var file = report.Files.Single();
        await Assert.That((file.BranchesHit, file.BranchesTotal)).IsEqualTo((0, 2));
        await Assert.That(file.ConditionsByLine).IsEmpty();
        var warning = await Assert.That(report.Warnings).HasSingleItem();
        await Assert.That(warning.Kind).IsEqualTo(CoverageWarningKind.MalformedConditionCoverage);
        await Assert.That(warning.Detail).IsEqualTo(
            "1 conditions with 2 covered outcomes disagree with condition-coverage (0/2) - using the line aggregate");
        await Assert.That(report.Evaluate(0, 100).Outcome).IsEqualTo(GateOutcome.Fail);

        var (code, stdout, stderr) = await Run("check", _ws.Write("lifted.cobertura.xml", doc), "--min-branch", "100");
        await Assert.That(code).IsEqualTo(1);
        await Assert.That(stdout + stderr).DoesNotContain("PASS");
    }

    [Test]
    [Arguments("lots", "5")]
    [Arguments("50%", "five")]
    public async Task UnparseableCondition_IsDroppedWithAWarning(string coverage, string number)
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

        await Assert.That(report.Files.Single().BranchesByLine[4]).IsEqualTo((1, 2));
        await Assert.That(report.Files.Single().ConditionsByLine).IsEmpty();
        var warning = await Assert.That(report.Warnings).HasSingleItem();
        await Assert.That(warning.Detail).IsEqualTo("a <condition> could not be parsed - using the line aggregate");
    }

    [Test]
    public async Task ConditionNumberRepeatedWithinALine_UnionsWithMax()
    {
        var report = Cobertura.NewDoc()
            .AddClass("src/X.cs", c => c.BranchWithConditions(4, "50% (1/2)", (5, "0%"), (5, "50%"), (5, "0%")))
            .Parse();

        await Assert.That(report.Files.Single().ConditionsByLine[4]).IsEquivalentTo(new Dictionary<int, int> { [5] = 1 });
        await Assert.That(report.Warnings).IsEmpty();
    }

    [Test]
    public async Task SwitchCondition_IsNotTwoWayDetail_AndKeepsTheAggregateSilently()
    {
        // Coverlet's switch: one condition for three outcomes. Not malformed, just not 2-way.
        var report = Cobertura.NewDoc()
            .AddClass("src/X.cs", c => c.BranchWithConditions(4, "66% (2/3)", (7, "66.67%")))
            .Parse();

        await Assert.That(report.Files.Single().BranchesByLine[4]).IsEqualTo((2, 3));
        await Assert.That(report.Files.Single().ConditionsByLine).IsEmpty();
        await Assert.That(report.Warnings).IsEmpty();
    }

    // ── A6: an empty class summary still decides the file's lines ─────────────

    [Test]
    [Arguments("<lines/>")]
    [Arguments("<lines></lines>")]
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

        foreach (var report in new[] { sync, async })
        {
            await Assert.That(report.Files.Single().LinesTotal).IsEqualTo(0);
            await Assert.That(report.Evaluate(80).Outcome).IsEqualTo(GateOutcome.NoData);
        }

        var (code, stdout, stderr) = await Run("check", _ws.Write("empty-summary.cobertura.xml", xml), "--min-line", "80");
        await Assert.That(code).IsEqualTo(DotCovCli.NoDataExitCode);
        await Assert.That(stdout + stderr).DoesNotContain("PASS");
        await Assert.That(stderr).StartsWith("NODATA:");
    }

    // ── A7: one file, however its relative name is spelled ───────────────────

    [Test]
    public async Task DotSegments_InFilenames_FoldIntoOneFile()
    {
        var report = Cobertura.NewDoc()
            .AddClass("./src/C.cs", "C", c => c.Line(1, hits: 1))
            .AddClass("src/C.cs", "C", c => c.Line(2, hits: 0))
            .AddClass("src/./C.cs", "C", c => c.Line(3, hits: 1))
            .AddClass(".//src/C.cs", "C", c => c.Line(4, hits: 1))
            .AddClass(@".\src\.\C.cs", "C", c => c.Line(5, hits: 1))
            .Parse();

        var file = await Assert.That(report.Files).HasSingleItem();
        await Assert.That(file.Path).IsEqualTo("src/C.cs");
        await Assert.That((file.LinesHit, file.LinesTotal)).IsEqualTo((4, 5));
    }

    [Test]
    public async Task DotSegments_RootsAndFilenamesFollowOneRule_ParentSegmentsAreKept()
    {
        var report = Cobertura.NewDoc()
            .WithSource("./repo/./src/.")
            .AddClass("./app/C.cs", "C", c => c.Line(1, hits: 1))
            .AddClass("app/../app/C.cs", "C", c => c.Line(2, hits: 1))
            .Parse();

        await Assert.That(report.SourceRoots).IsEquivalentTo(["repo/src"]);
        // `..` is never resolved: that needs the file system's view of links, and the disk is not read.
        await Assert.That(report.Files.Select(static f => f.Path))
            .IsEquivalentTo(["repo/src/app/C.cs", "repo/src/app/../app/C.cs"]);
    }

    [Test]
    [Arguments("./.")]
    [Arguments("/.")]
    public async Task DotSegments_RootOfOnlyDots_IsTheNoOpOrTheFilesystemRoot(string root)
    {
        var report = Cobertura.NewDoc().WithSource(root).AddClass("app/C.cs", "C", c => c.Line(1, hits: 1)).Parse();

        await Assert.That(report.Files.Single().Path).IsEqualTo(root is "/." ? "/app/C.cs" : "app/C.cs");
    }
}
