using System.Text;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

public sealed class CoberturaParserAsyncTests
{
    [Test]
    public async Task ParseAsync_SmallDocument_ReturnsEquivalentReportToSync()
    {
        var doc = Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c.Line(1, hits: 3).Line(2, hits: 0))
            .AddClass("src/B.cs", c => c.Line(5, hits: 1));

        var sync = doc.Parse();
        var async = await CoberturaParser.ParseAsync(doc.ToStream());

        await Assert.That(async.TotalLines).IsEqualTo(sync.TotalLines);
        await Assert.That(async.TotalLinesHit).IsEqualTo(sync.TotalLinesHit);
        await Assert.That(async.Files.Count).IsEqualTo(sync.Files.Count);
    }

    [Test]
    public async Task ParseAsync_RespectsCancellation()
    {
        using var stream = Cobertura.NewDoc()
            .AddClass("a.cs", c => c.Line(1, 1))
            .ToStream();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.That(async () => await CoberturaParser.ParseAsync(stream, ct: cts.Token)).Throws<OperationCanceledException>();
    }

    [Test]
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

        await Assert.ThrowsExactlyAsync<System.Xml.XmlException>(async () => await CoberturaParser.ParseAsync(stream));
    }

    [Test]
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

        var report = await CoberturaParser.ParseAsync(stream);

        await Assert.That(report.Files).HasSingleItem();
        await Assert.That(report.TotalLinesHit).IsEqualTo(1);
    }

    [Test]
    public async Task ParseAsync_EnforcesCharacterCap()
    {
        using var stream = Cobertura.NewDoc()
            .AddClass("a.cs", c => c.Line(1, 1))
            .ToStream();

        await Assert.ThrowsExactlyAsync<System.Xml.XmlException>(async () => await CoberturaParser.ParseAsync(stream, maxChars: 50));
    }

    [Test]
    public async Task ParseAsync_PartialBranch_RecordsBranchDetail()
    {
        using var stream = Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c.Branch(10, "50% (1/2)"))
            .ToStream();

        var report = await CoberturaParser.ParseAsync(stream);

        var partial = report.Files[0].PartialBranches.Single();
        await Assert.That(partial.Line).IsEqualTo(10);
        await Assert.That(partial.Covered).IsEqualTo(1);
        await Assert.That(partial.Total).IsEqualTo(2);
    }

    // A malformed condition string is NOT ignored quietly — the parser emits a
    // MalformedConditionCoverage warning. That contract (including BranchesTotal == 0)
    // is pinned by CoberturaParserTests.Parse_MalformedConditionString_EmitsWarning.

    [Test]
    public async Task Parse_LineWithoutNumber_IsSkipped()
    {
        var report = Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c.MalformedLine("", "5"))
            .Parse();

        await Assert.That(report.Files).HasSingleItem();
        await Assert.That(report.Files[0].LinesTotal).IsEqualTo(0);
    }

    [Test]
    public async Task Parse_ClassWithoutFilename_IsSkipped()
    {
        const string xml = """
                           <?xml version="1.0"?>
                           <coverage><packages><package><classes>
                             <class name="Anon"><lines><line number="1" hits="1" branch="false"/></lines></class>
                           </classes></package></packages></coverage>
                           """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var report = CoberturaParser.Parse(stream);

        await Assert.That(report.Files).IsEmpty();
    }

    [Test]
    public async Task Parse_NoBranchData_HasBranchDataFalse()
    {
        var report = Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c.Line(1, hits: 1).Line(2, hits: 0))
            .Parse();

        await Assert.That(report.HasBranchData).IsFalse();
        await Assert.That(report.Files[0].HasBranchData).IsFalse();
    }
}