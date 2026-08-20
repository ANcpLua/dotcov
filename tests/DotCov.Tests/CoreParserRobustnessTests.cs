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
    [InlineData("")]
    [InlineData("**/")]
    public void ParseDirectory_PatternWithDirectoryComponent_Throws(string pattern)
    {
        // The parameter is not a glob: any directory component used to be silently discarded,
        // so "coverage/*.xml" matched the wrong scope (or nothing) and flowed into Evaluate
        // as NoData — the most invisible misconfiguration. Only 'filename' and '**/filename'
        // are supported; everything else must throw. "" and "**/" are the empty-filename
        // holes: Directory.GetFiles(dir, "") matches nothing, so both silently returned an
        // empty report — the exact failure this gate exists to prevent.
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

    // ── C23: parse errors must name the offending file ──

    [Fact]
    public void ParseFile_MalformedXml_ExceptionMessageNamesTheFile()
    {
        // XmlException knows line/column but not which file. The library prefixes the path —
        // still as XmlException, the published contract type — so directory aggregates and
        // Nuke consumers get attribution without a CLI-side re-discovery fork.
        var root = Directory.CreateTempSubdirectory("dotcov-attr-").FullName;
        try
        {
            var path = Path.Combine(root, "bad.xml");
            File.WriteAllText(path, "<coverage><packa");

            var ex = Assert.Throws<XmlException>(() => CoberturaParser.ParseFile(path));

            Assert.StartsWith($"{path}: ", ex.Message, StringComparison.Ordinal);
            var inner = Assert.IsType<XmlException>(ex.InnerException);
            Assert.DoesNotContain(path, inner.Message);   // prefixed once, not recursively
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ParseFile_MalformedXml_RethrowKeepsLineAndPositionCoordinates()
    {
        // The path-prefixing rethrow must not cost the structured coordinates 0.0.2-era
        // library consumers read off the exception: LineNumber/LinePosition carry over from
        // the inner XmlException (via the 4-arg ctor), and the message stays byte-identical
        // to the plain "{path}: {inner.Message}" shape — the location sentence is stripped
        // before the ctor re-appends it, so it appears exactly once.
        var root = Directory.CreateTempSubdirectory("dotcov-coords-").FullName;
        try
        {
            var path = Path.Combine(root, "bad.xml");
            File.WriteAllText(path, "<coverage><packa");

            var ex = Assert.Throws<XmlException>(() => CoberturaParser.ParseFile(path));

            var inner = Assert.IsType<XmlException>(ex.InnerException);
            Assert.NotEqual(0, inner.LineNumber);
            Assert.Equal(inner.LineNumber, ex.LineNumber);
            Assert.Equal(inner.LinePosition, ex.LinePosition);
            Assert.Equal($"{path}: {inner.Message}", ex.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ParseDirectory_MalformedFileAmongSeveral_ExceptionMessageNamesTheOffender()
    {
        var root = Directory.CreateTempSubdirectory("dotcov-attr-dir-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "good"));
            Directory.CreateDirectory(Path.Combine(root, "bad"));
            File.WriteAllBytes(Path.Combine(root, "good", "coverage.cobertura.xml"),
                Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1)).ToBytes());
            var badPath = Path.Combine(root, "bad", "coverage.cobertura.xml");
            File.WriteAllText(badPath, "<coverage><packages>");

            var ex = Assert.Throws<XmlException>(() => CoberturaParser.ParseDirectory(root));

            // ParseDirectory routes through ParseFile, so the message carries exactly one
            // path prefix — the malformed report, not the healthy one.
            Assert.StartsWith($"{badPath}: ", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── C18: maxChars threading through the path-level entry points ──

    [Fact]
    public void ParseDirectory_MaxCharsOverload_EnforcesPerFileCap()
    {
        var root = Directory.CreateTempSubdirectory("dotcov-maxchars-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(root, "coverage.cobertura.xml"),
                Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1)).ToBytes());

            Assert.Throws<XmlException>(() =>
                CoberturaParser.ParseDirectory(root, "**/coverage.cobertura.xml", maxChars: 50));
            Assert.Single(
                CoberturaParser.ParseDirectory(root, "**/coverage.cobertura.xml", maxChars: 1_000_000).Files);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ParsePath_MaxCharsOverload_AppliesToBothFileAndDirectoryInputs()
    {
        var root = Directory.CreateTempSubdirectory("dotcov-maxchars-path-").FullName;
        try
        {
            var file = Path.Combine(root, "coverage.cobertura.xml");
            File.WriteAllBytes(file, Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1)).ToBytes());

            Assert.Throws<XmlException>(() => CoberturaParser.ParsePath(file, maxChars: 50));
            Assert.Throws<XmlException>(() => CoberturaParser.ParsePath(root, maxChars: 50));
            Assert.Single(CoberturaParser.ParsePath(file, maxChars: 1_000_000).Files);
            Assert.Single(CoberturaParser.ParsePath(root, maxChars: 1_000_000).Files);
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
