using System.Text;
using System.Xml;
using DotCov.Tests.Infrastructure;
using Xunit;

namespace DotCov.Tests;

/// <summary>
/// Malformed-input and misconfiguration behavior of the parsing entry points: the DoS
/// character cap on the sync path, garbage and truncated documents, a bad file inside a
/// directory aggregate, and the <c>ParseDirectory</c> pattern contract. Every case here is a
/// way for "nothing was measured" to masquerade as a clean empty report if left unpinned.
/// </summary>
public sealed class CoreParserRobustnessTests
{
    [Fact]
    public void Parse_Sync_EnforcesCharacterCap()
    {
        // The async twin is covered in CoberturaParserAsyncTests; the sync overload takes the
        // same maxChars and must enforce the same 50M-char-style DoS cap.
        using var stream = Cobertura.NewDoc()
            .AddClass("a.cs", c => c.Line(1, 1))
            .ToStream();

        Assert.Throws<XmlException>(() => CoberturaParser.Parse(stream, maxChars: 50));
    }

    [Fact]
    public void Parse_NonXmlPayload_ThrowsXmlException()
    {
        // Plain text must throw, not come back as a misleading empty CoverageReport that a
        // gate would then read as NoData instead of "your input is broken".
        using var stream = new MemoryStream("this is not xml at all"u8.ToArray());

        Assert.Throws<XmlException>(() => CoberturaParser.Parse(stream));
    }

    [Fact]
    public void Parse_TruncatedDocument_ThrowsXmlException()
    {
        // A stream cut mid-element (interrupted upload, partial CI artifact) is not a smaller
        // report — it must fail loudly rather than return the lines read so far.
        var full = Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c.Line(1, 1).Line(2, 0))
            .ToBytes();
        using var stream = new MemoryStream(full, 0, full.Length / 2);

        Assert.Throws<XmlException>(() => CoberturaParser.Parse(stream));
    }

    [Fact]
    public void ParseDirectory_OneMalformedFileAmongSeveral_PropagatesXmlException()
    {
        // Current contract, pinned: a malformed file inside the aggregate propagates its
        // XmlException out of ParseDirectory rather than being skipped — a broken artifact
        // must not silently shrink the merged report.
        var root = Directory.CreateTempSubdirectory("dotcov-robust-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "good"));
            Directory.CreateDirectory(Path.Combine(root, "bad"));
            File.WriteAllBytes(Path.Combine(root, "good", "coverage.cobertura.xml"),
                Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1)).ToBytes());
            File.WriteAllText(Path.Combine(root, "bad", "coverage.cobertura.xml"),
                "<coverage><packages>");   // truncated

            Assert.Throws<XmlException>(() => CoberturaParser.ParseDirectory(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("Fixtures/sample.cobertura.xml")]
    [InlineData("coverage/*.xml")]
    [InlineData("unit/**/coverage.cobertura.xml")]
    [InlineData(@"src\coverage.cobertura.xml")]
    public void ParseDirectory_PatternWithDirectoryComponent_Throws(string pattern)
    {
        // The parameter is not a glob: any directory component used to be silently discarded,
        // so "coverage/*.xml" matched the wrong scope (or nothing) and flowed into Evaluate
        // as NoData — the most invisible misconfiguration. Only 'filename' and '**/filename'
        // are supported; everything else must throw.
        var root = Directory.CreateTempSubdirectory("dotcov-pattern-").FullName;
        try
        {
            var ex = Assert.Throws<ArgumentException>(() => CoberturaParser.ParseDirectory(root, pattern));
            Assert.Equal("pattern", ex.ParamName);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ParseDirectory_SupportedShapes_StillWork()
    {
        // The two documented shapes must keep working after validation: bare filename
        // (top level only) and the recursive '**/' prefix.
        var root = Directory.CreateTempSubdirectory("dotcov-shapes-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "nested"));
            File.WriteAllBytes(Path.Combine(root, "coverage.cobertura.xml"),
                Cobertura.NewDoc().AddClass("top.cs", c => c.Line(1, 1)).ToBytes());
            File.WriteAllBytes(Path.Combine(root, "nested", "coverage.cobertura.xml"),
                Cobertura.NewDoc().AddClass("deep.cs", c => c.Line(1, 1)).ToBytes());

            Assert.Single(CoberturaParser.ParseDirectory(root, "coverage.cobertura.xml").Files);
            Assert.Equal(2, CoberturaParser.ParseDirectory(root, "**/coverage.cobertura.xml").Files.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
