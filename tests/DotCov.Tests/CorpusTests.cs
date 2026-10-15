namespace DotCov.Tests;
/// <summary>
/// Pins the parser against the emitter corpus in <c>Fixtures/Corpus</c> — miniature but
/// shape-faithful reports from the real-world Cobertura producers (gcovr, coverage.py,
/// cover2cover, grcov, ReportGenerator, Coverlet, the original Cobertura DTD example; see
/// <c>Fixtures/Corpus/README.md</c> for the sample → producer → upstream mapping). Every
/// expected number below is computed by hand from the sample's line/branch content, never
/// copied from the file's own summary attributes — the parser's semantics are the contract,
/// not the emitter's arithmetic.
/// </summary>
[TestClass]
public sealed class CorpusTests
{
    private static readonly string Corpus = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Corpus");
    // ── gcovr (C/C++) ─────────────────────────────────────────────────────────
    [TestMethod]
    public void Gcovr_SingleQuotedDeclAndDoctype_ParsesWithRootedKeyAndSaturatedHits()
    {
        // Lines 5,7,8,10,12 with hits 3e9,3e9,0,2,2 → 4/5 hit; branches 7:(1/2) + 12:(2/2) → 3/4.
        // The relative filename `src/calc.c` roots against the single <source> element.
        var report = CoberturaParser.Parse(ReportResolver.Resolve($"{Corpus}/gcovr/coverage.xml"));
        var calc = Assert.ContainsSingle(report.Files);
        Assert.AreEqual("/home/runner/work/myproj/myproj/src/calc.c", calc.Path);
        Assert.AreEqual(5, calc.LinesTotal);
        Assert.AreEqual(4, calc.LinesHit);
        Assert.AreEqual(4, calc.BranchesTotal);
        Assert.AreEqual(3, calc.BranchesHit);
        Assert.AreSequenceEqual([8], calc.UncoveredLines, SequenceOrder.InOrder);
        // 3,000,000,000 hits (gcovr's 64-bit counters) saturate to int.MaxValue, never wrap to
        // a negative that would flip a covered line to a miss.
        Assert.AreEqual(int.MaxValue, calc.LineHits[5]);
        Assert.IsEmpty(report.Warnings);
        Assert.AreSequenceEqual(["/home/runner/work/myproj/myproj"], report.SourceRoots, SequenceOrder.InOrder);
    }

    // ── coverage.py (Python) ──────────────────────────────────────────────────
    [TestMethod]
    public void CoveragePy_TwoModules_AggregateSemanticsMatchHandCount()
    {
        // module.py: lines 1-5,7,8 hits 1,1,1,1,0,1,0 → 5/7; branches 3:(1/2) + 7:(2/2) → 3/4.
        // __init__.py: 1/1. Report totals: 6/8 lines = 0.75, 3/4 branches.
        var report = CoberturaParser.Parse(ReportResolver.Resolve($"{Corpus}/coveragepy/coverage.xml"));
        Assert.AreEqual(2, report.Files.Count);
        var module = report.Files.Single(f => f.Path == "/home/runner/work/myproj/myproj/src/mypkg/module.py");
        Assert.AreEqual(7, module.LinesTotal);
        Assert.AreEqual(5, module.LinesHit);
        Assert.AreEqual(4, module.BranchesTotal);
        Assert.AreEqual(3, module.BranchesHit);
        var init = report.Files.Single(f => f.Path == "/home/runner/work/myproj/myproj/src/mypkg/__init__.py");
        Assert.AreEqual(1, init.LinesTotal);
        Assert.AreEqual(1, init.LinesHit);
        Assert.IsFalse(init.HasBranchData);
        Assert.AreEqual(0.75, report.LineRate);
        Assert.AreEqual(0.75, report.BranchRate);
        Assert.IsEmpty(report.Warnings);
    }

    // ── cover2cover (JaCoCo → Cobertura, Java) ────────────────────────────────
    [TestMethod]
    public void Cover2Cover_RelativeSourceRoot_PrefixesKeyAndKeepsJacocoBranchTotals()
    {
        // Lines 10,12,13,15,17 hits 1,1,1,1,0 → 4/5; branches 12:(1/3) + 15:(1/2) → 2/5.
        // The relative root `src/main/java` still prefixes the key — identity must be stable
        // whether or not the root happens to be absolute.
        var report = CoberturaParser.Parse(ReportResolver.Resolve($"{Corpus}/cover2cover/coverage.xml"));
        var foo = Assert.ContainsSingle(report.Files);
        Assert.AreEqual("src/main/java/com/example/Foo.java", foo.Path);
        Assert.AreEqual(5, foo.LinesTotal);
        Assert.AreEqual(4, foo.LinesHit);
        Assert.AreEqual(5, foo.BranchesTotal);
        Assert.AreEqual(2, foo.BranchesHit);
        Assert.AreEqual(0.4, report.BranchRate);
        Assert.IsEmpty(report.Warnings);
    }

    // ── grcov (Rust) ──────────────────────────────────────────────────────────
    [TestMethod]
    public void Grcov_NoOpSourceRootAndCountValuedConditions_KeepsLineAggregate()
    {
        // Lines 3,5,7,9,11 hits 1,7,7,2,0 → 4/5; branches 7:(1/2) + 9:(0/0) → 1/2.
        var report = CoberturaParser.Parse(ReportResolver.Resolve($"{Corpus}/grcov/coverage.xml"));
        var main = Assert.ContainsSingle(report.Files);
        // <source>.</source> is a no-op root: no prefix, no declared root on the report.
        Assert.AreEqual("src/main.rs", main.Path);
        Assert.IsEmpty(report.SourceRoots);
        Assert.AreEqual(5, main.LinesTotal);
        Assert.AreEqual(4, main.LinesHit);
        Assert.AreEqual(2, main.BranchesTotal);
        Assert.AreEqual(1, main.BranchesHit);
        // grcov writes <condition coverage="1"/> as a COUNT, not a percentage; read as 1% both
        // conditions derive 0 covered, so the 2-outcome consistency gate (2 conditions × 2 ≠
        // line total 2) must drop the per-condition detail and keep the honest 1/2 aggregate.
        Assert.IsFalse(main.ConditionsByLine.ContainsKey(7));
        Assert.AreEqual(0.5, report.BranchRate);
        Assert.IsEmpty(report.Warnings);
    }

    // ── ReportGenerator (merged .NET output) ──────────────────────────────────
    [TestMethod]
    public void ReportGenerator_ComplexityNaN_ParsesWithPerMethodLinePartitioning()
    {
        // Add: 10,11 hits 4,4; Div: 20,21,22,24 hits 2,2,1,0 → 5/6 lines.
        // Branches 11:(2/2) + 21:(1/3) → 3/5. complexity="NaN" must not disturb parsing.
        var report = CoberturaParser.Parse(ReportResolver.Resolve($"{Corpus}/reportgenerator/Cobertura.xml"));
        var calc = Assert.ContainsSingle(report.Files);
        Assert.AreEqual("/home/runner/work/app/src/MyApp/Calculator.cs", calc.Path);
        Assert.AreEqual(6, calc.LinesTotal);
        Assert.AreEqual(5, calc.LinesHit);
        Assert.AreEqual(5, calc.BranchesTotal);
        Assert.AreEqual(3, calc.BranchesHit);
        Assert.AreSequenceEqual([24], calc.UncoveredLines, SequenceOrder.InOrder);
        Assert.IsEmpty(report.Warnings);
    }

    // ── Reference Cobertura (coverage-04.dtd example) ─────────────────────────
    [TestMethod]
    public void ReferenceCobertura_DoctypeAndDriveLetterRoot_ParsesCanonicalShape()
    {
        // The canonical coverage-04.dtd document: DOCTYPE must be skipped (not rejected — the
        // format's own emitters write it), and the Windows drive-letter root must prefix the key.
        // Lines 12,13,16,17,19,24 hits 3,19,16,9,7,0 → 5/6; branches 13:(2/2) + 16:(1/2) → 3/4.
        var report = CoberturaParser.Parse(ReportResolver.Resolve($"{Corpus}/reference/cobertura-dtd-example.xml"));
        var search = Assert.ContainsSingle(report.Files);
        Assert.AreEqual("C:/local/mvn-project/src/main/java/search/BinarySearch.java", search.Path);
        Assert.AreEqual(6, search.LinesTotal);
        Assert.AreEqual(5, search.LinesHit);
        Assert.AreEqual(4, search.BranchesTotal);
        Assert.AreEqual(3, search.BranchesHit);
        Assert.AreEqual(5.0 / 6, report.LineRate);
        Assert.AreEqual(0.75, report.BranchRate);
        Assert.IsEmpty(report.Warnings);
    }

    // ── Monorepo: same relative name, genuinely different files ───────────────
    [TestMethod]
    public void Monorepo_SameRelativeNameUnderDifferentRoots_StaysTwoRootedFiles()
    {
        // svc-a app/main.py 8/10 and svc-b app/main.py 2/6 are DIFFERENT files. Rooting each
        // key against its report's <source> keeps them distinct: 10/16 lines = 62.5%, never
        // the silently-fused 8/10 that discarding the roots would produce.
        var report = CoberturaParser.Parse(ReportResolver.Resolve($"{Corpus}/monorepo"));
        Assert.AreEqual(2, report.Files.Count);
        var svcA = report.Files.Single(f => f.Path == "/home/runner/work/mono/mono/services/svc-a/app/main.py");
        Assert.AreEqual(10, svcA.LinesTotal);
        Assert.AreEqual(8, svcA.LinesHit);
        var svcB = report.Files.Single(f => f.Path == "/home/runner/work/mono/mono/services/svc-b/app/main.py");
        Assert.AreEqual(6, svcB.LinesTotal);
        Assert.AreEqual(2, svcB.LinesHit);
        Assert.AreEqual(16, report.TotalLines);
        Assert.AreEqual(10, report.TotalLinesHit);
        Assert.AreEqual(0.625, report.LineRate);
        // Different roots + shared file name: the merge cannot prove these are distinct files
        // (it cannot probe the producing disk), so the honest cross-root ambiguity warning
        // fires while both entries are kept.
        Assert.Contains(w => w.Kind == CoverageWarningKind.FileIdentityAmbiguous, report.Warnings);
    }

    // ── Path identity: the same file under two Coverlet conventions ───────────
    [TestMethod]
    public void PathIdentity_DefaultVsDeterministicSourcePaths_MergesWithAmbiguityWarning()
    {
        // The SAME Calculator.cs uploaded under Coverlet's default convention (root `/` +
        // machine-absolute filename) and DeterministicSourcePaths (root `/_/` + repo-relative
        // filename). No root arithmetic can unify the keys, so the merge keeps both entries
        // (totals double-count: 4/6 lines, 2/4 branches) and MUST surface the ambiguity.
        var report = CoberturaParser.Parse(ReportResolver.Resolve($"{Corpus}/pathidentity"));
        Assert.AreEqual(2, report.Files.Count);
        Assert.AreEqual(1, report.Files.Count(f => f.Path == "/home/runner/work/app/app/src/MyApp/Calculator.cs"));
        Assert.AreEqual(1, report.Files.Count(f => f.Path == "/_/src/MyApp/Calculator.cs"));
        Assert.AreEqual(6, report.TotalLines);
        Assert.AreEqual(4, report.TotalLinesHit);
        Assert.AreEqual(4, report.TotalBranches);
        Assert.AreEqual(2, report.TotalBranchesHit);
        var warning = report.Warnings.Single(w => w.Kind == CoverageWarningKind.FileIdentityAmbiguous);
        Assert.Contains("/home/runner/work/app/app/src/MyApp/Calculator.cs", warning.Detail);
        Assert.Contains("/_/src/MyApp/Calculator.cs", warning.Detail);
    }

    // ── Edge: empty packages ──────────────────────────────────────────────────
    [TestMethod]
    public void EmptyPackages_NothingMeasured_IsNoDataNotFullCoverage()
    {
        var report = CoberturaParser.Parse(ReportResolver.Resolve($"{Corpus}/edge/empty-packages.xml"));
        Assert.IsEmpty(report.Files);
        Assert.IsNull(report.LineRate);
        Assert.IsFalse(report.HasLineData);
        // "We measured nothing" must gate as NoData, never as a passing 100%.
        Assert.AreEqual(GateOutcome.NoData, report.Evaluate(80).Outcome);
    }

    // ── Edge: case-differing filenames are distinct files ─────────────────────
    [TestMethod]
    public void CaseSensitivePair_StaysTwoFilesAtFiftyPercent()
    {
        // linux/net/netfilter really contains both xt_TCPMSS.c (4/4) and xt_tcpmss.c (0/4).
        // Ordinal keying keeps them apart: 4/8 = 50%, not a case-fused 4/4 that erases the
        // uncovered file's misses.
        var report = CoberturaParser.Parse(ReportResolver.Resolve($"{Corpus}/edge/gcovr-case-sensitive.xml"));
        Assert.AreEqual(2, report.Files.Count);
        var upper = report.Files.Single(f => f.Path == "/home/runner/linux/net/netfilter/xt_TCPMSS.c");
        Assert.AreEqual(4, upper.LinesHit);
        var lower = report.Files.Single(f => f.Path == "/home/runner/linux/net/netfilter/xt_tcpmss.c");
        Assert.AreEqual(0, lower.LinesHit);
        Assert.AreEqual(0.5, report.LineRate);
    }

    // ── Edge: non-default file names need --pattern ───────────────────────────
    [TestMethod]
    public void NamedDir_DefaultPattern_SelectsCoberturaButNotGenericCoverageXml()
    {
        var report = CoberturaParser.Parse(ReportResolver.Resolve($"{Corpus}/edge/gcovr-named-dir"));
        Assert.AreEqual(8, report.TotalLines);
        Assert.AreEqual(6, report.TotalLinesHit);
        Assert.AreEqual(4, report.TotalBranches);
    }

    [TestMethod]
    public void NamedDir_ExplicitPatterns_ParseEachProducersFile()
    {
        // coverage.xml is the gcovr sample (4/5 lines, 3/4 branches); Cobertura.xml is the
        // coverage.py sample (6/8 lines, 3/4 branches). Each pattern selects exactly one.
        var gcovr = CoberturaParser.Parse(ReportResolver.ResolveDirectory($"{Corpus}/edge/gcovr-named-dir", ReportPattern.Parse("coverage.xml")));
        Assert.AreEqual(5, gcovr.TotalLines);
        Assert.AreEqual(4, gcovr.TotalLinesHit);
        Assert.AreEqual(3, gcovr.TotalBranchesHit);
        var coveragePy = CoberturaParser.Parse(ReportResolver.ResolveDirectory($"{Corpus}/edge/gcovr-named-dir", ReportPattern.Parse("cobertura.xml")));
        Assert.AreEqual(8, coveragePy.TotalLines);
        Assert.AreEqual(6, coveragePy.TotalLinesHit);
        Assert.AreEqual(4, coveragePy.TotalBranches);
    }

    // ── Whole-corpus sweep ────────────────────────────────────────────────────
    [TestMethod]
    public void Corpus_ContainsAllFourteenSamples()
    {
        Assert.AreEqual(14, Samples().Count());
    }

    [TestMethod]
    [DynamicData(nameof(Samples))]
    public void Sample_ParsesWithoutThrowing(string relativePath)
    {
        var report = CoberturaParser.Parse(ReportInput.FromFile(Path.Combine(Corpus, relativePath)));
        Assert.IsNotNull(report);
    }

    public static IEnumerable<string> Samples() => Directory.EnumerateFiles(Corpus, "*.xml", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(Corpus, path).Replace('\\', '/')).Order(StringComparer.Ordinal);
}
