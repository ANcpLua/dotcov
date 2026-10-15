using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;
/// <summary>
/// Pins the opt-in <c>CoberturaParser.ParseMethods</c> family: per-method entries stay
/// DISTINCT (the exact opposite of the class-level parse's dedup-into-file-sets semantics),
/// file keys join against <see cref = "FileCoverage.Path"/>, and only usable complexity survives.
/// Fixture expectations are hand-counted from the corpus files, never copied from their
/// summary attributes.
/// </summary>
[TestClass]
public sealed class MethodCoverageParseTests
{
    private const string Corpus = "Fixtures/Corpus";
    // ── Real emitter files ────────────────────────────────────────────────────
    [TestMethod]
    public void ReportGenerator_TwoMethods_StayPerMethodDistinct()
    {
        // The real coverlet/ReportGenerator shape: Add (lines 10,11 both hit, complexity 1) and
        // Div (lines 20,21,22 hit + 24 missed, complexity 2) under one class. The class-level
        // parse fuses all six lines into one file; THIS parse must keep two distinct entries.
        var methods = CoberturaParser.ParseMethods(ReportInput.FromFile($"{Corpus}/reportgenerator/Cobertura.xml")).Methods;
        Assert.AreEqual(2, methods.Count);
        var add = methods.Single(m => m.MethodName == "Add");
        Assert.AreEqual("MyApp.Calculator", add.ClassName);
        Assert.AreEqual("(System.Int32,System.Int32)", add.Signature);
        Assert.AreEqual("/home/runner/work/app/src/MyApp/Calculator.cs", add.File); // <source> root applied
        Assert.AreEqual(10, add.StartLine);
        Assert.AreEqual(11, add.EndLine);
        Assert.AreEqual(2, add.LinesHit);
        Assert.AreEqual(2, add.LinesTotal);
        Assert.AreEqual(1, add.Complexity);
        Assert.AreEqual(1.0, add.LineRate);
        var div = methods.Single(m => m.MethodName == "Div");
        Assert.AreEqual(20, div.StartLine);
        Assert.AreEqual(24, div.EndLine);
        Assert.AreEqual(3, div.LinesHit);
        Assert.AreEqual(4, div.LinesTotal);
        Assert.AreEqual(2, div.Complexity);
        Assert.AreEqual(0, div.LineHits[24]);
    }

    [TestMethod]
    public void CoverletShape_MethodFileKey_MatchesClassLevelPath()
    {
        // pathidentity/job-a: <source>/</source> + relative filename — the method entry's File
        // must resolve through the same root arithmetic as the class-level FileCoverage.Path,
        // or CRAP rows would name files no coverage report contains.
        var methods = CoberturaParser.ParseMethods(ReportInput.FromFile($"{Corpus}/pathidentity/job-a/coverage.cobertura.xml")).Methods;
        var report = CoberturaParser.Parse(ReportInput.FromFile($"{Corpus}/pathidentity/job-a/coverage.cobertura.xml"));
        var add = Assert.ContainsSingle(methods);
        Assert.AreEqual("Add", add.MethodName);
        Assert.AreEqual(2, add.Complexity); // coverlet's real per-method complexity
        Assert.AreEqual(2, add.LinesHit); // lines 10,11 hit; 12 missed
        Assert.AreEqual(3, add.LinesTotal);
        Assert.AreEqual(report.Files.Single().Path, add.File);
    }

    [TestMethod]
    public void Gcovr_PlaceholderComplexityZero_IsNotMeasured()
    {
        // gcovr writes complexity="0.0" on every method — a placeholder, not a measurement
        // (cyclomatic complexity is >= 1 by construction). It must surface as null, or every
        // C/C++ method would CRAP-score 0 and the gate would wave through anything.
        var methods = CoberturaParser.ParseMethods(ReportInput.FromFile($"{Corpus}/gcovr/coverage.xml")).Methods;
        Assert.IsNotEmpty(methods);
        Assert.IsTrue((methods).All(m => m.Complexity == null));
    }

    [TestMethod]
    public void ReferenceDtdExample_NoComplexityAttribute_IsNull()
    {
        var methods = CoberturaParser.ParseMethods(ReportInput.FromFile($"{Corpus}/reference/cobertura-dtd-example.xml")).Methods;
        Assert.IsNotEmpty(methods);
        Assert.IsTrue((methods).All(m => m.Complexity == null));
    }

    [TestMethod]
    public void SampleWithoutMethodsElement_ReturnsEmpty_NotThrow()
    {
        var methods = CoberturaParser.ParseMethods(ReportInput.FromFile("Fixtures/sample.cobertura.xml")).Methods;
        Assert.IsEmpty(methods);
    }

    // ── Builder-driven semantics ──────────────────────────────────────────────
    [TestMethod]
    public void ClassLevelLinesSummary_DoesNotLeakIntoMethods()
    {
        // The trailing class-level <lines> repeats every method line; folding it in would
        // double-attribute lines to whichever method the cursor last visited.
        var methods = Cobertura.NewDoc().AddClass("src/A.cs", c => c.Method("M", "()", "1", m => m.Line(1, hits: 1)).Line(1, hits: 1).Line(50, hits: 0)).ParseMethods();
        var m = Assert.ContainsSingle(methods);
        Assert.AreEqual(1, m.LinesTotal);
        Assert.IsFalse(m.LineHits.ContainsKey(50));
    }

    [TestMethod]
    public void SameMethodAcrossClassBlocks_MergesPerLineWithMax()
    {
        // Partial classes / re-emitted blocks: same (file, class, method, signature) key must
        // union per line with Math.Max, mirroring the class-level parse.
        var methods = Cobertura.NewDoc().AddClass("src/A.cs", "MyApp.A", c => c.Method("M", "()", "2", m => m.Line(1, hits: 0).Line(2, hits: 3))).AddClass("src/A.cs", "MyApp.A", c => c.Method("M", "()", "2", m => m.Line(1, hits: 5))).ParseMethods();
        var m = Assert.ContainsSingle(methods);
        Assert.AreEqual(5, m.LineHits[1]);
        Assert.AreEqual(3, m.LineHits[2]);
        Assert.AreEqual(2, m.LinesHit);
        Assert.AreEqual(2, m.Complexity);
    }

    [TestMethod]
    public void DifferentSignatures_StayDistinctEntries()
    {
        var methods = Cobertura.NewDoc().AddClass("src/A.cs", "MyApp.A", c => c.Method("M", "(System.Int32)", "1", m => m.Line(1, hits: 1)).Method("M", "(System.Int32,System.Int32)", "3", m => m.Line(5, hits: 0))).ParseMethods();
        Assert.AreEqual(2, methods.Count);
        Assert.AreEqual(2, methods.Select(m => m.Signature).Distinct().Count());
    }

    [TestMethod]
    public void MalformedComplexity_NaNOrText_IsNull()
    {
        var methods = Cobertura.NewDoc().AddClass("src/A.cs", "MyApp.A", c => c.Method("N", "()", "NaN", m => m.Line(1, hits: 1)).Method("T", "()", "abc", m => m.Line(2, hits: 1))).ParseMethods();
        Assert.IsTrue((methods).All(m => m.Complexity == null));
    }

    [TestMethod]
    public void MalformedMethodLine_NumberSkipped_HitsDegradeToZero()
    {
        // Same policy as the class-level parse: an unparseable line number cannot be recorded
        // at all; an unparseable hits value degrades to 0 rather than dropping the line.
        var methods = Cobertura.NewDoc().AddClass("src/A.cs", "MyApp.A", c => c.Method("M", "()", "1", m => m.Line(1, hits: 3).MalformedLine("abc", "1").MalformedLine("7", "many"))).ParseMethods();
        var m = Assert.ContainsSingle(methods);
        Assert.AreEqual(2, m.LinesTotal); // line "abc" is unrepresentable; line 7 survives
        Assert.AreEqual(3, m.LineHits[1]);
        Assert.AreEqual(0, m.LineHits[7]); // "many" degrades to 0, the line itself is kept
    }

    [TestMethod]
    public void MethodWithoutLines_HasZeroRangeAndNullRate()
    {
        var methods = Cobertura.NewDoc().AddClass("src/A.cs", "MyApp.A", c => c.Method("Empty", "()", "1", _ =>
        {
        })).ParseMethods();
        var m = Assert.ContainsSingle(methods);
        Assert.AreEqual(0, m.StartLine);
        Assert.AreEqual(0, m.EndLine);
        Assert.AreEqual(0, m.LinesTotal);
        Assert.IsNull(m.LineRate);
    }

    [TestMethod]
    public async Task ParseMethodsDirectory_MergesSameMethodAcrossFiles()
    {
        using var temp = TempWorkspace.Create("dotcov-methods-dir-");
        await File.WriteAllBytesAsync(temp.PrepareFile("a.cobertura.xml"), Cobertura.NewDoc().AddClass("src/A.cs", "MyApp.A", c => c.Method("M", "()", "2", m => m.Line(1, hits: 1).Line(2, hits: 0))).ToBytes());
        await File.WriteAllBytesAsync(temp.PrepareFile("b.cobertura.xml"), Cobertura.NewDoc().AddClass("src/A.cs", "MyApp.A", c => c.Method("M", "()", "2", m => m.Line(1, hits: 0).Line(2, hits: 4))).ToBytes());
        var methods = CoberturaParser.ParseMethods(ReportResolver.ResolveDirectory(temp.Root, ReportPattern.Parse("*.cobertura.xml"))).Methods;
        var m = Assert.ContainsSingle(methods);
        Assert.AreEqual(2, m.LinesHit); // union-with-max: both lines covered across the two runs
        Assert.AreEqual(1, m.LineHits[1]);
        Assert.AreEqual(4, m.LineHits[2]);
    }

    // ── MethodCoverageReport: identity, roots, diagnostics ────────────────────
    [TestMethod]
    public void ParseMethods_Identity_IsFileClassNameAndSignature()
    {
        // Same (file, class, name, signature) merges per line with Math.Max; a differing
        // signature (overload) or class stays a distinct entry, in first-seen order.
        var report = Cobertura.NewDoc().AddClass("src/A.cs", "MyApp.A", c => c.Method("M", "(System.Int32)", "2", m => m.Line(1, hits: 1).Line(2, hits: 0)).Method("M", "(System.String)", "3", m => m.Line(5, hits: 0))).AddClass("src/A.cs", "MyApp.A", c => c.Method("M", "(System.Int32)", "4", m => m.Line(1, hits: 0).Line(2, hits: 7))).AddClass("src/A.cs", "MyApp.B", c => c.Method("M", "(System.Int32)", "1", m => m.Line(9, hits: 1))).ParseMethods();
        Assert.AreSequenceEqual(["MyApp.A.M(System.Int32)", "MyApp.A.M(System.String)", "MyApp.B.M(System.Int32)"], report.Select(m => $"{m.ClassName}.{m.MethodName}{m.Signature}"), SequenceOrder.InOrder);
        var merged = report[0];
        Assert.AreEqual(1, merged.LineHits[1]);
        Assert.AreEqual(7, merged.LineHits[2]);
        Assert.AreEqual(2, merged.LinesHit);
        Assert.AreEqual(4, merged.Complexity); // Math.Max across the two blocks
    }

    [TestMethod]
    public void ParseMethods_SourceRoots_FollowTheFileReportRules()
    {
        // A lone no-op root exposes no roots; real roots are kept in declared order and
        // deduplicated by normalized identity across inputs, exactly like CoverageReport.
        var noOp = ReportInput.FromBytes("noop", Cobertura.NewDoc().WithSource(".").AddClass("a.cs", "A", c => c.Method("M", "()", "1", m => m.Line(1, hits: 1))).ToBytes());
        var rooted = ReportInput.FromBytes("rooted", Cobertura.NewDoc().WithSource("/repo").WithSource("/other").AddClass("a.cs", "A", c => c.Method("M", "()", "1", m => m.Line(1, hits: 1))).ToBytes());
        var respelled = ReportInput.FromBytes("respelled", Cobertura.NewDoc().WithSource("/repo/").AddClass("a.cs", "A", c => c.Method("M", "()", "1", m => m.Line(1, hits: 1))).ToBytes());
        Assert.IsEmpty(CoberturaParser.ParseMethods(noOp).SourceRoots);
        var report = CoberturaParser.ParseMethods([rooted, respelled]);
        Assert.AreSequenceEqual(["/repo", "/other"], report.SourceRoots, SequenceOrder.InOrder);
        Assert.AreSequenceEqual(["/repo/a.cs"], report.Methods.Select(m => m.File), SequenceOrder.InOrder);
    }

    [TestMethod]
    public void ParseMethods_Warnings_AreCompleteAcrossInputs()
    {
        // Every decoding anomaly of every document, in encounter order: malformed hits inside
        // a method, a multi-root declaration, and a second document's malformed hits.
        var first = ReportInput.FromBytes("first", Cobertura.NewDoc().WithSource("/a").WithSource("/b").AddClass("x.cs", "X", c => c.Method("M", "()", "1", m => m.MalformedLine("3", "NaN"))).ToBytes());
        var second = ReportInput.FromBytes("second", Cobertura.NewDoc().AddClass("y.cs", "Y", c => c.Method("N", "()", "1", m => m.MalformedLine("4", "?"))).ToBytes());
        var report = CoberturaParser.ParseMethods([first, second]);
        Assert.AreSequenceEqual([(CoverageWarningKind.FileIdentityAmbiguous, "/a/x.cs", 0), (CoverageWarningKind.MalformedHits, "/a/x.cs", 3), (CoverageWarningKind.MalformedHits, "y.cs", 4), ], report.Warnings.Select(w => (w.Kind, w.File, w.Line)), SequenceOrder.InOrder);
        Assert.AreSequenceEqual(["/a/x.cs"], report.AmbiguousFiles.Select(a => a.Path), SequenceOrder.InAnyOrder);
        Assert.AreSequenceEqual([0, 0], report.Methods.Select(m => m.LineHits.Values.Single()), SequenceOrder.InOrder);
    }

    [TestMethod]
    public async Task ParseMethodsFile_MalformedXml_ThrowsReportParseExceptionNamingTheFile()
    {
        // Same structured error contract as the file-level parse, so directory aggregates
        // name the malformed report.
        using var temp = TempWorkspace.Create("dotcov-methods-bad-");
        var path = temp.PrepareFile("bad.cobertura.xml");
        await File.WriteAllTextAsync(path, "<coverage><unclosed>");
        var ex = Assert.ThrowsExactly<ReportParseException>(() => CoberturaParser.ParseMethods(ReportInput.FromFile(path)));
        Assert.AreEqual(path, ex.SourceName);
    }

    [TestMethod]
    public async Task ParseMethodsDirectory_MalformedFileInDirectory_NamesTheMalformedFile()
    {
        using var temp = TempWorkspace.Create("dotcov-methods-dir-bad-");
        var good = temp.PrepareFile("a.cobertura.xml");
        await File.WriteAllBytesAsync(good, Cobertura.NewDoc().AddClass("src/A.cs", "MyApp.A", c => c.Method("M", "()", "1", m => m.Line(1, hits: 1))).ToBytes());
        var bad = temp.PrepareFile("b.cobertura.xml");
        await File.WriteAllTextAsync(bad, "<coverage><packages>");
        // The assertion executes and completes within the captured resource lifetime.
        // ReSharper disable once AccessToDisposedClosure
        var ex = Assert.ThrowsExactly<ReportParseException>(() => CoberturaParser.ParseMethods(ReportResolver.ResolveDirectory(temp.Root, ReportPattern.Parse("*.cobertura.xml"))));
        Assert.AreEqual(bad, ex.SourceName);
    }
}
