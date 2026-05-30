using System.Text;
using DotCov.Tests.Infrastructure;
using Xunit;

namespace DotCov.Tests;

public sealed class CoberturaParserAsyncTests
{
    [Fact]
    public async Task ParseAsync_SmallDocument_ReturnsEquivalentReportToSync()
    {
        var doc = Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c.Line(1, hits: 3).Line(2, hits: 0))
            .AddClass("src/B.cs", c => c.Line(5, hits: 1));

        var sync = doc.Parse();
        var async = await CoberturaParser.ParseAsync(doc.ToStream());

        Assert.Equal(sync.TotalLines, async.TotalLines);
        Assert.Equal(sync.TotalLinesHit, async.TotalLinesHit);
        Assert.Equal(sync.Files.Count, async.Files.Count);
    }

    [Fact]
    public async Task ParseAsync_RespectsCancellation()
    {
        using var stream = Cobertura.NewDoc()
            .AddClass("a.cs", c => c.Line(1, 1))
            .ToStream();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await CoberturaParser.ParseAsync(stream, ct: cts.Token));
    }

    [Fact]
    public async Task ParseAsync_RejectsDtd()
    {
        const string malicious = """
                                 <?xml version="1.0"?>
                                 <!DOCTYPE x [<!ENTITY e SYSTEM "file:///etc/passwd">]>
                                 <coverage><packages></packages></coverage>
                                 """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(malicious));

        await Assert.ThrowsAsync<System.Xml.XmlException>(
            async () => await CoberturaParser.ParseAsync(stream));
    }

    [Fact]
    public async Task ParseAsync_EnforcesCharacterCap()
    {
        using var stream = Cobertura.NewDoc()
            .AddClass("a.cs", c => c.Line(1, 1))
            .ToStream();

        await Assert.ThrowsAsync<System.Xml.XmlException>(
            async () => await CoberturaParser.ParseAsync(stream, maxChars: 50));
    }

    [Fact]
    public async Task ParseAsync_PartialBranch_RecordsBranchDetail()
    {
        using var stream = Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c.Branch(10, "50% (1/2)"))
            .ToStream();

        var report = await CoberturaParser.ParseAsync(stream);

        var partial = report.Files[0].PartialBranches.Single();
        Assert.Equal(10, partial.Line);
        Assert.Equal(1, partial.Covered);
        Assert.Equal(2, partial.Total);
    }

    [Fact]
    public void Parse_MalformedConditionString_IsIgnoredQuietly()
    {
        var report = Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c.Branch(10, "garbage"))
            .Parse();

        Assert.Single(report.Files);
        Assert.Equal(0, report.Files[0].BranchesTotal);
    }

    [Fact]
    public void Parse_LineWithoutNumber_IsSkipped()
    {
        var report = Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c.MalformedLine("", "5"))
            .Parse();

        Assert.Single(report.Files);
        Assert.Equal(0, report.Files[0].LinesTotal);
    }

    [Fact]
    public void Parse_ClassWithoutFilename_IsSkipped()
    {
        const string xml = """
                           <?xml version="1.0"?>
                           <coverage><packages><package><classes>
                             <class name="Anon"><lines><line number="1" hits="1" branch="false"/></lines></class>
                           </classes></package></packages></coverage>
                           """;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        var report = CoberturaParser.Parse(stream);

        Assert.Empty(report.Files);
    }

    [Fact]
    public void Parse_NoBranchData_HasBranchDataFalse()
    {
        var report = Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c.Line(1, hits: 1).Line(2, hits: 0))
            .Parse();

        Assert.False(report.HasBranchData);
        Assert.False(report.Files[0].HasBranchData);
    }
}
