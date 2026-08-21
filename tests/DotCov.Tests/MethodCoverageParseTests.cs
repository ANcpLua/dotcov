using DotCov.Tests.Infrastructure;
using Xunit;

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

    [Fact]
    public void ReportGenerator_TwoMethods_StayPerMethodDistinct()
    {
        // The real coverlet/ReportGenerator shape: Add (lines 10,11 both hit, complexity 1) and
        // Div (lines 20,21,22 hit + 24 missed, complexity 2) under one class. The class-level
        // parse fuses all six lines into one file; THIS parse must keep two distinct entries.
        var methods = CoberturaParser.ParseMethodsFile($"{Corpus}/reportgenerator/Cobertura.xml");

        Assert.Equal(2, methods.Count);

        var add = methods.Single(m => m.MethodName == "Add");
        Assert.Equal("MyApp.Calculator", add.ClassName);
        Assert.Equal("(System.Int32,System.Int32)", add.Signature);
        Assert.Equal("/home/runner/work/app/src/MyApp/Calculator.cs", add.File);   // <source> root applied
        Assert.Equal(10, add.StartLine);
        Assert.Equal(11, add.EndLine);
        Assert.Equal(2, add.LinesHit);
        Assert.Equal(2, add.LinesTotal);
        Assert.Equal(1, add.Complexity);
        Assert.Equal(1.0, add.LineRate);

        var div = methods.Single(m => m.MethodName == "Div");
        Assert.Equal(20, div.StartLine);
        Assert.Equal(24, div.EndLine);
        Assert.Equal(3, div.LinesHit);
        Assert.Equal(4, div.LinesTotal);
        Assert.Equal(2, div.Complexity);
        Assert.Equal(0, div.LineHits[24]);
    }

    [Fact]
    public void CoverletShape_MethodFileKey_MatchesClassLevelPath()
    {
        // pathidentity/job-a: <source>/</source> + relative filename — the method entry's File
        // must resolve through the same root arithmetic as the class-level FileCoverage.Path,
        // or CRAP rows would name files no coverage report contains.
        var methods = CoberturaParser.ParseMethodsFile($"{Corpus}/pathidentity/job-a/coverage.cobertura.xml");
        var report = CoberturaParser.ParseFile($"{Corpus}/pathidentity/job-a/coverage.cobertura.xml");

        var add = Assert.Single(methods);
        Assert.Equal("Add", add.MethodName);
        Assert.Equal(2, add.Complexity);           // coverlet's real per-method complexity
        Assert.Equal(2, add.LinesHit);             // lines 10,11 hit; 12 missed
        Assert.Equal(3, add.LinesTotal);
        Assert.Equal(Assert.Single(report.Files).Path, add.File);
    }

    [Fact]
    public void Gcovr_PlaceholderComplexityZero_IsNotMeasured()
    {
        // gcovr writes complexity="0.0" on every method — a placeholder, not a measurement
        // (cyclomatic complexity is >= 1 by construction). It must surface as null, or every
        // C/C++ method would CRAP-score 0 and the gate would wave through anything.
        var methods = CoberturaParser.ParseMethodsFile($"{Corpus}/gcovr/coverage.xml");

        Assert.NotEmpty(methods);
        Assert.All(methods, m => Assert.Null(m.Complexity));
    }

    [Fact]
    public void ReferenceDtdExample_NoComplexityAttribute_IsNull()
    {
        var methods = CoberturaParser.ParseMethodsFile($"{Corpus}/reference/cobertura-dtd-example.xml");

        Assert.NotEmpty(methods);
        Assert.All(methods, m => Assert.Null(m.Complexity));
    }

    [Fact]
    public void SampleWithoutMethodsElement_ReturnsEmpty_NotThrow()
    {
        var methods = CoberturaParser.ParseMethodsFile("Fixtures/sample.cobertura.xml");

        Assert.Empty(methods);
    }

    // ── Builder-driven semantics ──────────────────────────────────────────────

    [Fact]
    public void ClassLevelLinesSummary_DoesNotLeakIntoMethods()
    {
        // The trailing class-level <lines> repeats every method line; folding it in would
        // double-attribute lines to whichever method the cursor last visited.
        var methods = Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c
                .Method("M", "()", "1", m => m.Line(1, hits: 1))
                .Line(1, hits: 1)
                .Line(50, hits: 0))
            .ParseMethods();

        var m = Assert.Single(methods);
        Assert.Equal(1, m.LinesTotal);
        Assert.False(m.LineHits.ContainsKey(50));
    }

    [Fact]
    public void SameMethodAcrossClassBlocks_MergesPerLineWithMax()
    {
        // Partial classes / re-emitted blocks: same (file, class, method, signature) key must
        // union per line with Math.Max, mirroring the class-level parse.
        var methods = Cobertura.NewDoc()
            .AddClass("src/A.cs", "MyApp.A", c => c.Method("M", "()", "2", m => m.Line(1, hits: 0).Line(2, hits: 3)))
            .AddClass("src/A.cs", "MyApp.A", c => c.Method("M", "()", "2", m => m.Line(1, hits: 5)))
            .ParseMethods();

        var m = Assert.Single(methods);
        Assert.Equal(5, m.LineHits[1]);
        Assert.Equal(3, m.LineHits[2]);
        Assert.Equal(2, m.LinesHit);
        Assert.Equal(2, m.Complexity);
    }

    [Fact]
    public void DifferentSignatures_StayDistinctEntries()
    {
        var methods = Cobertura.NewDoc()
            .AddClass("src/A.cs", "MyApp.A", c => c
                .Method("M", "(System.Int32)", "1", m => m.Line(1, hits: 1))
                .Method("M", "(System.Int32,System.Int32)", "3", m => m.Line(5, hits: 0)))
            .ParseMethods();

        Assert.Equal(2, methods.Count);
        Assert.Equal(2, methods.Select(m => m.Signature).Distinct().Count());
    }

    [Fact]
    public void MalformedComplexity_NaNOrText_IsNull()
    {
        var methods = Cobertura.NewDoc()
            .AddClass("src/A.cs", "MyApp.A", c => c
                .Method("N", "()", "NaN", m => m.Line(1, hits: 1))
                .Method("T", "()", "abc", m => m.Line(2, hits: 1)))
            .ParseMethods();

        Assert.All(methods, m => Assert.Null(m.Complexity));
    }

    [Fact]
    public void MethodWithoutLines_HasZeroRangeAndNullRate()
    {
        var methods = Cobertura.NewDoc()
            .AddClass("src/A.cs", "MyApp.A", c => c.Method("Empty", "()", "1", _ => { }))
            .ParseMethods();

        var m = Assert.Single(methods);
        Assert.Equal(0, m.StartLine);
        Assert.Equal(0, m.EndLine);
        Assert.Equal(0, m.LinesTotal);
        Assert.Null(m.LineRate);
    }

    [Fact]
    public void ParseMethodsDirectory_MergesSameMethodAcrossFiles()
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

            var m = Assert.Single(methods);
            Assert.Equal(2, m.LinesHit);   // union-with-max: both lines covered across the two runs
            Assert.Equal(1, m.LineHits[1]);
            Assert.Equal(4, m.LineHits[2]);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ParseMethodsDirectory_UnsupportedPattern_Throws()
    {
        var dir = Directory.CreateTempSubdirectory("dotcov-methods-pattern-");
        try
        {
            // Shares ParseDirectory's single pattern gate — same rejection, same message shape.
            Assert.Throws<ArgumentException>(() =>
                CoberturaParser.ParseMethodsDirectory(dir.FullName, "sub/dir/coverage.xml"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void ParseMethodsPath_MissingPath_ThrowsFileNotFound()
    {
        Assert.Throws<FileNotFoundException>(() =>
            CoberturaParser.ParseMethodsPath("/nonexistent/nowhere.xml"));
    }
}
