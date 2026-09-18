using System.Text;

namespace DotCov.Tests;

public sealed class ParserSourceTests
{
    private static CoverageReport ParseXml(string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return CoberturaParser.Parse(stream);
    }

    /// <summary>One-class document with optional <c>&lt;source&gt;</c> roots (raw, unescaped).</summary>
    private static string Doc(
        string sources,
        string filename,
        string lines = """<line number="1" hits="1" branch="false" />""") =>
        $"""
         <?xml version="1.0"?>
         <coverage>{sources}<packages><package><classes>
           <class name="X" filename="{filename}"><lines>{lines}</lines></class>
         </classes></package></packages></coverage>
         """;

    [Test]
    public async Task Parse_EmptySourceElement_DoesNotSwallowTheFollowingRoot()
    {
        // An empty source must not consume the next root and lose its filename prefix.
        var report = ParseXml(Doc("<sources><source/><source>/repo</source></sources>", "app/main.py"));

        await Assert.That(report.SourceRoots.Single()).IsEqualTo("/repo");
        await Assert.That(report.Files.Single().Path).IsEqualTo("/repo/app/main.py");
    }

    [Test]
    public async Task Parse_CommentInsideSource_IsNotARoot()
    {
        // Comments inside a source element are not filesystem paths.
        var report = ParseXml(Doc(
            "<sources><source><!--ci checkout--></source></sources>", "app/main.py"));

        await Assert.That(report.SourceRoots).IsEmpty();
        await Assert.That(report.Files.Single().Path).IsEqualTo("app/main.py");
    }

    [Test]
    public async Task Parse_MultipleRoots_WarningIsReportScopedWithEmptyFile()
    {
        // Ambiguous roots concern the report, not a particular file or line.
        var report = ParseXml(Doc("<sources><source>/a</source><source>/b</source></sources>", "x.cs"));

        var w = await Assert.That(report.Warnings).HasSingleItem();
        await Assert.That(w.Kind).IsEqualTo(CoverageWarningKind.FileIdentityAmbiguous);
        await Assert.That(w.File).IsEqualTo("");
        await Assert.That(w.Line).IsEqualTo(0);
        await Assert.That(w.Detail).Contains("'/a'");
    }
}
