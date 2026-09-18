using System.Text;
using System.Xml;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

/// <summary>
/// Malformed-input behavior of the parsing entry points: the DoS character cap on the sync
/// path, garbage and truncated documents, and a bad file inside a directory aggregate. Every
/// case here is a way for "nothing was measured" to masquerade as a clean empty report if
/// left unpinned. Pattern validation lives in <see cref="ReportPatternTests"/>, input
/// resolution in <see cref="ReportResolverTests"/>, and cross-source parity in
/// <see cref="ParserContractTests"/>.
/// </summary>
public sealed class CoreParserRobustnessTests
{
    [Test]
    public void Parse_Sync_EnforcesCharacterCap()
    {
        // The async twin is covered in CoberturaParserAsyncTests; the sync overload takes the
        // same maxChars and must enforce the same 50M-char-style DoS cap.
        using var stream = Cobertura.NewDoc()
            .AddClass("a.cs", c => c.Line(1, 1))
            .ToStream();

        Assert.ThrowsExactly<XmlException>(() => CoberturaParser.Parse(stream, maxChars: 50));
    }

    [Test]
    public void Parse_NonXmlPayload_ThrowsXmlException()
    {
        // Plain text must throw, not come back as a misleading empty CoverageReport that a
        // gate would then read as NoData instead of "your input is broken".
        using var stream = new MemoryStream("this is not xml at all"u8.ToArray());

        Assert.ThrowsExactly<XmlException>(() => CoberturaParser.Parse(stream));
    }

    [Test]
    public void Parse_TruncatedDocument_ThrowsXmlException()
    {
        // A stream cut mid-element (interrupted upload, partial CI artifact) is not a smaller
        // report — it must fail loudly rather than return the lines read so far.
        var full = Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c.Line(1, 1).Line(2, 0))
            .ToBytes();
        using var stream = new MemoryStream(full, 0, full.Length / 2);

        Assert.ThrowsExactly<XmlException>(() => CoberturaParser.Parse(stream));
    }

    [Test]
    public void ParseDirectory_OneMalformedFileAmongSeveral_PropagatesXmlException()
    {
        // Current contract, pinned: a malformed file inside the aggregate propagates its
        // XmlException out of ParseDirectory rather than being skipped — a broken artifact
        // must not silently shrink the merged report.
        using var temp = TempWorkspace.Create("dotcov-robust-");
        File.WriteAllBytes(temp.PrepareFile("good/coverage.cobertura.xml"),
            Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1)).ToBytes());
        File.WriteAllText(temp.PrepareFile("bad/coverage.cobertura.xml"),
            "<coverage><packages>");   // truncated

        Assert.ThrowsExactly<ReportParseException>(() => CoberturaParser.Parse(ReportResolver.ResolveDirectory(temp.Root, ReportPattern.Default)));
    }

    // ── C23: parse errors must name the offending file ──

    [Test]
    public async Task ParseFile_MalformedXml_ExceptionNamesTheFile()
    {
        // XmlException knows line/column but not which file. The library wraps it in a
        // ReportParseException carrying the source name as data, so directory aggregates and
        // build adapters get attribution without re-discovering the file.
        using var temp = TempWorkspace.Create("dotcov-attr-");
        var path = temp.PrepareFile("bad.xml");
        await File.WriteAllTextAsync(path, "<coverage><packa");

        var ex = Assert.ThrowsExactly<ReportParseException>(() => CoberturaParser.Parse(ReportInput.FromFile(path)));

        await Assert.That(ex.SourceName).IsEqualTo(path);
        await Assert.That(ex.Message).DoesNotContain(path);   // the message is the reader's, unmodified
        await Assert.That(ex.InnerException.Message).IsEqualTo(ex.Message);
    }

    [Test]
    public async Task ParseFile_MalformedXml_KeepsLineAndPositionCoordinates()
    {
        // The wrapper must not cost the structured coordinates consumers read off the
        // exception: LineNumber/LinePosition carry over from the inner XmlException.
        using var temp = TempWorkspace.Create("dotcov-coords-");
        var path = temp.PrepareFile("bad.xml");
        await File.WriteAllTextAsync(path, "<coverage><packa");

        var ex = Assert.ThrowsExactly<ReportParseException>(() => CoberturaParser.Parse(ReportInput.FromFile(path)));

        var inner = ex.InnerException;
        await Assert.That(inner.LineNumber).IsNotEqualTo(0);
        await Assert.That(ex.LineNumber).IsEqualTo(inner.LineNumber);
        await Assert.That(ex.LinePosition).IsEqualTo(inner.LinePosition);
    }

    [Test]
    public async Task ParseDirectory_MalformedFileAmongSeveral_ExceptionMessageNamesTheOffender()
    {
        using var temp = TempWorkspace.Create("dotcov-attr-dir-");
        await File.WriteAllBytesAsync(temp.PrepareFile("good/coverage.cobertura.xml"),
            Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1)).ToBytes());
        var badPath = temp.PrepareFile("bad/coverage.cobertura.xml");
        await File.WriteAllTextAsync(badPath, "<coverage><packages>");

        var ex = Assert.ThrowsExactly<ReportParseException>(() => CoberturaParser.Parse(ReportResolver.ResolveDirectory(temp.Root, ReportPattern.Default)));

        // The exception names the malformed report, not the healthy one.
        await Assert.That(ex.SourceName).IsEqualTo(badPath);
    }
}
