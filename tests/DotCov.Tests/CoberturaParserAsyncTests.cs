using System.Text;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;
[TestClass]
public sealed class CoberturaParserAsyncTests(TestContext testContext)
{
    [TestMethod]
    public async Task ParseAsync_SmallDocument_ReturnsEquivalentReportToSync()
    {
        var doc = Cobertura.NewDoc().AddClass("src/A.cs", c => c.Line(1, hits: 3).Line(2, hits: 0)).AddClass("src/B.cs", c => c.Line(5, hits: 1));
        var sync = doc.Parse();
        await using var stream = doc.ToStream();
        var async = await CoberturaParser.ParseAsync(stream, ct: testContext.CancellationToken);
        Assert.AreEqual(sync.TotalLines, async.TotalLines);
        Assert.AreEqual(sync.TotalLinesHit, async.TotalLinesHit);
        Assert.HasCount(sync.Files.Count, async.Files);
    }

    [TestMethod]
    public async Task ParseAsync_MethodOnlyClass_YieldsTheSameReportAsSync()
    {
        // A class whose only lines sit under <methods> (no class-level <lines>): the sync walk
        // let the method lines stand in for the file at the class end; the async walk never
        // finished the class and reported the file with no lines at all.
        var doc = Cobertura.NewDoc().AddClass("src/A.cs", "Ns.A", c => c.Method("M", "()", "2", m => m.Line(3, hits: 1).Line(4, hits: 0)).Method("N", "()", "1", m => m.Line(8, hits: 2))).AddClass("src/B.cs", c => c.Line(1, hits: 1));
        var sync = doc.Parse();
        await using var stream = doc.ToStream();
        var async = await CoberturaParser.ParseAsync(stream, ct: testContext.CancellationToken);
        Assert.AreSequenceEqual(sync.Files.Select(Shape), async.Files.Select(Shape), SequenceOrder.InAnyOrder);
        Assert.AreSequenceEqual(sync.Warnings, async.Warnings, SequenceOrder.InAnyOrder);
        var a = Assert.ContainsSingle(f => f.Path == "src/A.cs", async.Files);
        Assert.AreEqual((2, 3), (a.LinesHit, a.LinesTotal));
        Assert.AreSequenceEqual([3, 4, 8], a.LineHits.Keys, SequenceOrder.InAnyOrder);
    }

    private static (string Path, int LinesHit, int LinesTotal, int BranchesHit, int BranchesTotal, string Lines) Shape(FileCoverage f) => (f.Path, f.LinesHit, f.LinesTotal, f.BranchesHit, f.BranchesTotal, string.Join(",", f.LineHits.OrderBy(static kv => kv.Key).Select(static kv => $"{kv.Key}:{kv.Value}")));
    [TestMethod]
    public async Task ParseAsync_RespectsCancellation()
    {
        await using var stream = Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1)).ToStream();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var token = cts.Token;
        // The assertion executes and completes within the captured resource lifetime.
        // ReSharper disable once AccessToDisposedClosure
        await Assert.ThrowsAsync<OperationCanceledException>(async () => await CoberturaParser.ParseAsync(stream, ct: token));
    }

    [TestMethod]
    public async Task ParseAsync_XxeEntityReference_Throws()
    {
        // With DtdProcessing.Ignore the DTD itself is skipped, so the entity reference in
        // content is undeclared and the reader throws — XXE cannot pull external content.
        const string malicious = """
                                 <?xml version="1.0"?>
                                 <!DOCTYPE coverage [<!ENTITY e SYSTEM "file:///etc/passwd">]>
                                 <coverage><packages><package><classes>
                                   <class name="&e;" filename="x.cs"/>
                                 </classes></package></packages></coverage>
                                 """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(malicious));
        await Assert.ThrowsExactlyAsync<System.Xml.XmlException>(async () => await CoberturaParser.ParseAsync(stream, ct: testContext.CancellationToken));
    }

    [TestMethod]
    public async Task ParseAsync_BenignDoctype_Parses()
    {
        // Reference Cobertura emits a DOCTYPE on every report; skipping it (not dying on it)
        // is what lets the async path read the format's canonical emitters too.
        const string canonical = """
                                 <?xml version="1.0"?>
                                 <!DOCTYPE coverage SYSTEM "http://cobertura.sourceforge.net/xml/coverage-04.dtd">
                                 <coverage><packages><package><classes>
                                   <class name="X" filename="x.cs"><lines>
                                     <line number="1" hits="1" branch="false"/>
                                   </lines></class>
                                 </classes></package></packages></coverage>
                                 """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(canonical));
        var report = await CoberturaParser.ParseAsync(stream, ct: testContext.CancellationToken);
        Assert.ContainsSingle(report.Files);
        Assert.AreEqual(1, report.TotalLinesHit);
    }

    [TestMethod]
    public async Task ParseAsync_EnforcesCharacterCap()
    {
        await using var stream = Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1)).ToStream();
        await Assert.ThrowsExactlyAsync<System.Xml.XmlException>(async () => await CoberturaParser.ParseAsync(stream, maxChars: 50, ct: testContext.CancellationToken));
    }

    [TestMethod]
    public async Task ParseAsync_PartialBranch_RecordsBranchDetail()
    {
        await using var stream = Cobertura.NewDoc().AddClass("src/A.cs", c => c.Branch(10, "50% (1/2)")).ToStream();
        var report = await CoberturaParser.ParseAsync(stream, ct: testContext.CancellationToken);
        var partial = Assert.ContainsSingle(report.Files[0].PartialBranches);
        Assert.AreEqual(10, partial.Line);
        Assert.AreEqual(1, partial.Covered);
        Assert.AreEqual(2, partial.Total);
    }

    [TestMethod]
    public async Task ParseAsync_LineWithoutNumber_IsSkipped()
    {
        await using var stream = Cobertura.NewDoc().AddClass("src/A.cs", c => c.MalformedLine("", "5")).ToStream();
        var report = await CoberturaParser.ParseAsync(stream, ct: testContext.CancellationToken);
        Assert.ContainsSingle(report.Files);
        Assert.AreEqual(0, report.Files[0].LinesTotal);
    }

    [TestMethod]
    public async Task ParseAsync_ClassWithoutFilename_IsSkipped()
    {
        const string xml = """
                           <?xml version="1.0"?>
                           <coverage><packages><package><classes>
                             <class name="Anon"><lines><line number="1" hits="1" branch="false"/></lines></class>
                           </classes></package></packages></coverage>
                           """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var report = await CoberturaParser.ParseAsync(stream, ct: testContext.CancellationToken);
        Assert.IsEmpty(report.Files);
    }

    [TestMethod]
    public async Task ParseAsync_NoBranchData_HasBranchDataFalse()
    {
        await using var stream = Cobertura.NewDoc().AddClass("src/A.cs", c => c.Line(1, hits: 1).Line(2, hits: 0)).ToStream();
        var report = await CoberturaParser.ParseAsync(stream, ct: testContext.CancellationToken);
        Assert.IsFalse(report.HasBranchData);
        Assert.IsFalse(report.Files[0].HasBranchData);
    }
}
