using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

/// <summary>
/// Pins the opt-in <see cref="CoberturaParser.ParseMethods"/> family: per-method entries stay
/// DISTINCT (the exact opposite of the class-level parse's dedup-into-file-sets semantics),
/// file keys join against <see cref="FileCoverage.Path"/>, and only usable complexity survives.
/// Fixture expectations are hand-counted from the corpus files, never copied from their
/// summary attributes.
/// </summary>
public sealed class MethodCoverageParseTests
{
    private const string Corpus = "Fixtures/Corpus";

    // ── Real emitter files ────────────────────────────────────────────────────

    [Test]
    public async Task ReportGenerator_TwoMethods_StayPerMethodDistinct()
    {
        // The real coverlet/ReportGenerator shape: Add (lines 10,11 both hit, complexity 1) and
        // Div (lines 20,21,22 hit + 24 missed, complexity 2) under one class. The class-level
        // parse fuses all six lines into one file; THIS parse must keep two distinct entries.
        var methods = CoberturaParser.ParseMethodsFile($"{Corpus}/reportgenerator/Cobertura.xml");

        await Assert.That(methods.Count).IsEqualTo(2);

        var add = methods.Single(m => m.MethodName == "Add");
        await Assert.That(add.ClassName).IsEqualTo("MyApp.Calculator");
        await Assert.That(add.Signature).IsEqualTo("(System.Int32,System.Int32)");
        await Assert.That(add.File).IsEqualTo("/home/runner/work/app/src/MyApp/Calculator.cs");   // <source> root applied
        await Assert.That(add.StartLine).IsEqualTo(10);
        await Assert.That(add.EndLine).IsEqualTo(11);
        await Assert.That(add.LinesHit).IsEqualTo(2);
        await Assert.That(add.LinesTotal).IsEqualTo(2);
        await Assert.That(add.Complexity).IsEqualTo(1);
        await Assert.That(add.LineRate).IsEqualTo(1.0);

        var div = methods.Single(m => m.MethodName == "Div");
        await Assert.That(div.StartLine).IsEqualTo(20);
        await Assert.That(div.EndLine).IsEqualTo(24);
        await Assert.That(div.LinesHit).IsEqualTo(3);
        await Assert.That(div.LinesTotal).IsEqualTo(4);
        await Assert.That(div.Complexity).IsEqualTo(2);
        await Assert.That(div.LineHits[24]).IsEqualTo(0);
    }

    [Test]
    public async Task CoverletShape_MethodFileKey_MatchesClassLevelPath()
    {
        // pathidentity/job-a: <source>/</source> + relative filename — the method entry's File
        // must resolve through the same root arithmetic as the class-level FileCoverage.Path,
        // or CRAP rows would name files no coverage report contains.
        var methods = CoberturaParser.ParseMethodsFile($"{Corpus}/pathidentity/job-a/coverage.cobertura.xml");
        var report = CoberturaParser.ParseFile($"{Corpus}/pathidentity/job-a/coverage.cobertura.xml");

        var add = await Assert.That(methods).HasSingleItem();
        await Assert.That(add.MethodName).IsEqualTo("Add");
        await Assert.That(add.Complexity).IsEqualTo(2);           // coverlet's real per-method complexity
        await Assert.That(add.LinesHit).IsEqualTo(2);             // lines 10,11 hit; 12 missed
        await Assert.That(add.LinesTotal).IsEqualTo(3);
        await Assert.That(add.File).IsEqualTo(report.Files.Single().Path);
    }

    [Test]
    public async Task Gcovr_PlaceholderComplexityZero_IsNotMeasured()
    {
        // gcovr writes complexity="0.0" on every method — a placeholder, not a measurement
        // (cyclomatic complexity is >= 1 by construction). It must surface as null, or every
        // C/C++ method would CRAP-score 0 and the gate would wave through anything.
        var methods = CoberturaParser.ParseMethodsFile($"{Corpus}/gcovr/coverage.xml");

        await Assert.That(methods).IsNotEmpty();
        await Assert.That(methods).All(m => m.Complexity == null);
    }

    [Test]
    public async Task ReferenceDtdExample_NoComplexityAttribute_IsNull()
    {
        var methods = CoberturaParser.ParseMethodsFile($"{Corpus}/reference/cobertura-dtd-example.xml");

        await Assert.That(methods).IsNotEmpty();
        await Assert.That(methods).All(m => m.Complexity == null);
    }

    [Test]
    public async Task SampleWithoutMethodsElement_ReturnsEmpty_NotThrow()
    {
        var methods = CoberturaParser.ParseMethodsFile("Fixtures/sample.cobertura.xml");

        await Assert.That(methods).IsEmpty();
    }

    // ── Builder-driven semantics ──────────────────────────────────────────────

    [Test]
    public async Task ClassLevelLinesSummary_DoesNotLeakIntoMethods()
    {
        // The trailing class-level <lines> repeats every method line; folding it in would
        // double-attribute lines to whichever method the cursor last visited.
        var methods = Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c
                .Method("M", "()", "1", m => m.Line(1, hits: 1))
                .Line(1, hits: 1)
                .Line(50, hits: 0))
            .ParseMethods();

        var m = await Assert.That(methods).HasSingleItem();
        await Assert.That(m.LinesTotal).IsEqualTo(1);
        await Assert.That(m.LineHits.ContainsKey(50)).IsFalse();
    }

    [Test]
    public async Task SameMethodAcrossClassBlocks_MergesPerLineWithMax()
    {
        // Partial classes / re-emitted blocks: same (file, class, method, signature) key must
        // union per line with Math.Max, mirroring the class-level parse.
        var methods = Cobertura.NewDoc()
            .AddClass("src/A.cs", "MyApp.A", c => c.Method("M", "()", "2", m => m.Line(1, hits: 0).Line(2, hits: 3)))
            .AddClass("src/A.cs", "MyApp.A", c => c.Method("M", "()", "2", m => m.Line(1, hits: 5)))
            .ParseMethods();

        var m = await Assert.That(methods).HasSingleItem();
        await Assert.That(m.LineHits[1]).IsEqualTo(5);
        await Assert.That(m.LineHits[2]).IsEqualTo(3);
        await Assert.That(m.LinesHit).IsEqualTo(2);
        await Assert.That(m.Complexity).IsEqualTo(2);
    }

    [Test]
    public async Task DifferentSignatures_StayDistinctEntries()
    {
        var methods = Cobertura.NewDoc()
            .AddClass("src/A.cs", "MyApp.A", c => c
                .Method("M", "(System.Int32)", "1", m => m.Line(1, hits: 1))
                .Method("M", "(System.Int32,System.Int32)", "3", m => m.Line(5, hits: 0)))
            .ParseMethods();

        await Assert.That(methods.Count).IsEqualTo(2);
        await Assert.That(methods.Select(m => m.Signature).Distinct().Count()).IsEqualTo(2);
    }

    [Test]
    public async Task MalformedComplexity_NaNOrText_IsNull()
    {
        var methods = Cobertura.NewDoc()
            .AddClass("src/A.cs", "MyApp.A", c => c
                .Method("N", "()", "NaN", m => m.Line(1, hits: 1))
                .Method("T", "()", "abc", m => m.Line(2, hits: 1)))
            .ParseMethods();

        await Assert.That(methods).All(m => m.Complexity == null);
    }

    [Test]
    public async Task MalformedMethodLine_NumberSkipped_HitsDegradeToZero()
    {
        // Same policy as the class-level parse: an unparseable line number cannot be recorded
        // at all; an unparseable hits value degrades to 0 rather than dropping the line.
        var methods = Cobertura.NewDoc()
            .AddClass("src/A.cs", "MyApp.A", c => c
                .Method("M", "()", "1", m => m
                    .Line(1, hits: 3)
                    .MalformedLine("abc", "1")
                    .MalformedLine("7", "many")))
            .ParseMethods();

        var m = await Assert.That(methods).HasSingleItem();
        await Assert.That(m.LinesTotal).IsEqualTo(2);   // line "abc" is unrepresentable; line 7 survives
        await Assert.That(m.LineHits[1]).IsEqualTo(3);
        await Assert.That(m.LineHits[7]).IsEqualTo(0);  // "many" degrades to 0, the line itself is kept
    }

    [Test]
    public async Task MethodWithoutLines_HasZeroRangeAndNullRate()
    {
        var methods = Cobertura.NewDoc()
            .AddClass("src/A.cs", "MyApp.A", c => c.Method("Empty", "()", "1", _ => { }))
            .ParseMethods();

        var m = await Assert.That(methods).HasSingleItem();
        await Assert.That(m.StartLine).IsEqualTo(0);
        await Assert.That(m.EndLine).IsEqualTo(0);
        await Assert.That(m.LinesTotal).IsEqualTo(0);
        await Assert.That(m.LineRate).IsNull();
    }

    [Test]
    public async Task ParseMethodsDirectory_MergesSameMethodAcrossFiles()
    {
        var dir = Directory.CreateTempSubdirectory("dotcov-methods-dir-");
        try
        {
            File.WriteAllBytes(Path.Combine(dir.FullName, "a.cobertura.xml"), Cobertura.NewDoc()
                .AddClass("src/A.cs", "MyApp.A", c => c.Method("M", "()", "2", m => m.Line(1, hits: 1).Line(2, hits: 0)))
                .ToBytes());
            File.WriteAllBytes(Path.Combine(dir.FullName, "b.cobertura.xml"), Cobertura.NewDoc()
                .AddClass("src/A.cs", "MyApp.A", c => c.Method("M", "()", "2", m => m.Line(1, hits: 0).Line(2, hits: 4)))
                .ToBytes());

            var methods = CoberturaParser.ParseMethodsDirectory(dir.FullName, "*.cobertura.xml");

            var m = await Assert.That(methods).HasSingleItem();
            await Assert.That(m.LinesHit).IsEqualTo(2);   // union-with-max: both lines covered across the two runs
            await Assert.That(m.LineHits[1]).IsEqualTo(1);
            await Assert.That(m.LineHits[2]).IsEqualTo(4);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Test]
    public void ParseMethodsDirectory_UnsupportedPattern_Throws()
    {
        var dir = Directory.CreateTempSubdirectory("dotcov-methods-pattern-");
        try
        {
            // Shares ParseDirectory's single pattern gate — same rejection, same message shape.
            Assert.ThrowsExactly<ArgumentException>(() =>
                CoberturaParser.ParseMethodsDirectory(dir.FullName, "sub/dir/coverage.xml"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Test]
    public void ParseMethodsPath_MissingPath_ThrowsFileNotFound()
    {
        Assert.ThrowsExactly<FileNotFoundException>(() =>
            CoberturaParser.ParseMethodsPath("/nonexistent/nowhere.xml"));
    }

    [Test]
    public async Task ParseMethodsPath_DispatchesToFileAndDirectory()
    {
        // Same file-or-directory dispatch as ParsePath: both arms must land on the same parse.
        var dir = Directory.CreateTempSubdirectory("dotcov-methods-path-");
        try
        {
            var file = Path.Combine(dir.FullName, "coverage.cobertura.xml");
            File.WriteAllBytes(file, Cobertura.NewDoc()
                .AddClass("src/A.cs", "MyApp.A", c => c.Method("M", "()", "2", m => m.Line(1, hits: 1)))
                .ToBytes());

            var viaFile = CoberturaParser.ParseMethodsPath(file);
            var viaDirectory = CoberturaParser.ParseMethodsPath(dir.FullName);

            await Assert.That(viaFile.Single().MethodName).IsEqualTo("M");
            await Assert.That(viaDirectory.Single().MethodName).IsEqualTo("M");
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Test]
    public async Task ParseMethodsFile_MalformedXml_RethrowsWithPathPrefixed()
    {
        // Same path-prefixing rethrow contract as ParseFile, so directory aggregates name the
        // malformed report.
        var dir = Directory.CreateTempSubdirectory("dotcov-methods-bad-");
        try
        {
            var path = Path.Combine(dir.FullName, "bad.cobertura.xml");
            File.WriteAllText(path, "<coverage><unclosed>");

            var ex = Assert.ThrowsExactly<System.Xml.XmlException>(() => CoberturaParser.ParseMethodsFile(path));
            await Assert.That(ex.Message).StartsWith(path);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Test]
    public async Task ParseMethodsDirectory_MalformedFileInDirectory_RethrowsWithPathPrefixed()
    {
        var dir = Directory.CreateTempSubdirectory("dotcov-methods-dir-bad-");
        try
        {
            var good = Path.Combine(dir.FullName, "a.cobertura.xml");
            File.WriteAllBytes(good, Cobertura.NewDoc()
                .AddClass("src/A.cs", "MyApp.A", c => c.Method("M", "()", "1", m => m.Line(1, hits: 1)))
                .ToBytes());
            var bad = Path.Combine(dir.FullName, "b.cobertura.xml");
            File.WriteAllText(bad, "<coverage><packages>");

            var ex = Assert.ThrowsExactly<System.Xml.XmlException>(() =>
                CoberturaParser.ParseMethodsDirectory(dir.FullName, "*.cobertura.xml"));
            await Assert.That(ex.Message).StartsWith(bad);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}