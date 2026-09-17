using TUnit.Assertions.Enums;
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
        var methods = CoberturaParser.ParseMethods(ReportInput.FromFile($"{Corpus}/reportgenerator/Cobertura.xml")).Methods;

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
        var methods = CoberturaParser.ParseMethods(ReportInput.FromFile($"{Corpus}/pathidentity/job-a/coverage.cobertura.xml")).Methods;
        var report = CoberturaParser.Parse(ReportInput.FromFile($"{Corpus}/pathidentity/job-a/coverage.cobertura.xml"));

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
        var methods = CoberturaParser.ParseMethods(ReportInput.FromFile($"{Corpus}/gcovr/coverage.xml")).Methods;

        await Assert.That(methods).IsNotEmpty();
        await Assert.That(methods).All(m => m.Complexity == null);
    }

    [Test]
    public async Task ReferenceDtdExample_NoComplexityAttribute_IsNull()
    {
        var methods = CoberturaParser.ParseMethods(ReportInput.FromFile($"{Corpus}/reference/cobertura-dtd-example.xml")).Methods;

        await Assert.That(methods).IsNotEmpty();
        await Assert.That(methods).All(m => m.Complexity == null);
    }

    [Test]
    public async Task SampleWithoutMethodsElement_ReturnsEmpty_NotThrow()
    {
        var methods = CoberturaParser.ParseMethods(ReportInput.FromFile("Fixtures/sample.cobertura.xml")).Methods;

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

            var methods = CoberturaParser.ParseMethods(ReportResolver.ResolveDirectory(dir.FullName, ReportPattern.Parse("*.cobertura.xml"))).Methods;

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

    // ── MethodCoverageReport: identity, roots, diagnostics ────────────────────

    [Test]
    public async Task ParseMethods_Identity_IsFileClassNameAndSignature()
    {
        // Same (file, class, name, signature) merges per line with Math.Max; a differing
        // signature (overload) or class stays a distinct entry, in first-seen order.
        var report = Cobertura.NewDoc()
            .AddClass("src/A.cs", "MyApp.A", c => c
                .Method("M", "(System.Int32)", "2", m => m.Line(1, hits: 1).Line(2, hits: 0))
                .Method("M", "(System.String)", "3", m => m.Line(5, hits: 0)))
            .AddClass("src/A.cs", "MyApp.A", c => c
                .Method("M", "(System.Int32)", "4", m => m.Line(1, hits: 0).Line(2, hits: 7)))
            .AddClass("src/A.cs", "MyApp.B", c => c
                .Method("M", "(System.Int32)", "1", m => m.Line(9, hits: 1)))
            .ParseMethods();

        await Assert.That(report.Select(m => $"{m.ClassName}.{m.MethodName}{m.Signature}"))
            .IsEquivalentTo(["MyApp.A.M(System.Int32)", "MyApp.A.M(System.String)", "MyApp.B.M(System.Int32)"], CollectionOrdering.Matching);
        var merged = report[0];
        await Assert.That(merged.LineHits[1]).IsEqualTo(1);
        await Assert.That(merged.LineHits[2]).IsEqualTo(7);
        await Assert.That(merged.LinesHit).IsEqualTo(2);
        await Assert.That(merged.Complexity).IsEqualTo(4);   // Math.Max across the two blocks
    }

    [Test]
    public async Task ParseMethods_SourceRoots_FollowTheFileReportRules()
    {
        // A lone no-op root exposes no roots; real roots are kept in declared order and
        // deduplicated by normalized identity across inputs, exactly like CoverageReport.
        var noOp = ReportInput.FromBytes("noop", Cobertura.NewDoc().WithSource(".")
            .AddClass("a.cs", "A", c => c.Method("M", "()", "1", m => m.Line(1, hits: 1))).ToBytes());
        var rooted = ReportInput.FromBytes("rooted", Cobertura.NewDoc().WithSource("/repo").WithSource("/other")
            .AddClass("a.cs", "A", c => c.Method("M", "()", "1", m => m.Line(1, hits: 1))).ToBytes());
        var respelled = ReportInput.FromBytes("respelled", Cobertura.NewDoc().WithSource("/repo/")
            .AddClass("a.cs", "A", c => c.Method("M", "()", "1", m => m.Line(1, hits: 1))).ToBytes());

        await Assert.That(CoberturaParser.ParseMethods(noOp).SourceRoots).IsEmpty();

        var report = CoberturaParser.ParseMethods([rooted, respelled]);
        await Assert.That(report.SourceRoots).IsEquivalentTo(["/repo", "/other"], CollectionOrdering.Matching);
        await Assert.That(report.Methods.Select(m => m.File)).IsEquivalentTo(["/repo/a.cs"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ParseMethods_Warnings_AreCompleteAcrossInputs()
    {
        // Every decoding anomaly of every document, in encounter order: malformed hits inside
        // a method, a multi-root declaration, and a second document's malformed hits.
        var first = ReportInput.FromBytes("first", Cobertura.NewDoc().WithSource("/a").WithSource("/b")
            .AddClass("x.cs", "X", c => c.Method("M", "()", "1", m => m.MalformedLine("3", "NaN"))).ToBytes());
        var second = ReportInput.FromBytes("second", Cobertura.NewDoc()
            .AddClass("y.cs", "Y", c => c.Method("N", "()", "1", m => m.MalformedLine("4", "?"))).ToBytes());

        var report = CoberturaParser.ParseMethods([first, second]);

        await Assert.That(report.Warnings.Select(w => (w.Kind, w.File, w.Line))).IsEquivalentTo(
        [
            (CoverageWarningKind.FileIdentityAmbiguous, "", 0),
            (CoverageWarningKind.MalformedHits, "/a/x.cs", 3),
            (CoverageWarningKind.MalformedHits, "y.cs", 4),
        ], CollectionOrdering.Matching);
        await Assert.That(report.Methods.Select(m => m.LineHits.Values.Single())).IsEquivalentTo([0, 0], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ParseMethodsFile_MalformedXml_ThrowsReportParseExceptionNamingTheFile()
    {
        // Same structured error contract as the file-level parse, so directory aggregates
        // name the malformed report.
        var dir = Directory.CreateTempSubdirectory("dotcov-methods-bad-");
        try
        {
            var path = Path.Combine(dir.FullName, "bad.cobertura.xml");
            File.WriteAllText(path, "<coverage><unclosed>");

            var ex = Assert.ThrowsExactly<ReportParseException>(() => CoberturaParser.ParseMethods(ReportInput.FromFile(path)));
            await Assert.That(ex.SourceName).IsEqualTo(path);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Test]
    public async Task ParseMethodsDirectory_MalformedFileInDirectory_NamesTheMalformedFile()
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

            var ex = Assert.ThrowsExactly<ReportParseException>(() =>
                CoberturaParser.ParseMethods(ReportResolver.ResolveDirectory(dir.FullName, ReportPattern.Parse("*.cobertura.xml"))));
            await Assert.That(ex.SourceName).IsEqualTo(bad);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }
}