using Xunit;

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
public sealed class CorpusTests
{
    private const string Corpus = "Fixtures/Corpus";

    // ── gcovr (C/C++) ─────────────────────────────────────────────────────────

    [Fact]
    public void Gcovr_SingleQuotedDeclAndDoctype_ParsesWithRootedKeyAndSaturatedHits()
    {
        // Lines 5,7,8,10,12 with hits 3e9,3e9,0,2,2 → 4/5 hit; branches 7:(1/2) + 12:(2/2) → 3/4.
        // The relative filename `src/calc.c` roots against the single <source> element.
        var report = CoberturaParser.ParsePath($"{Corpus}/gcovr/coverage.xml");

        var calc = Assert.Single(report.Files);
        Assert.Equal("/home/runner/work/myproj/myproj/src/calc.c", calc.Path);
        Assert.Equal(5, calc.LinesTotal);
        Assert.Equal(4, calc.LinesHit);
        Assert.Equal(4, calc.BranchesTotal);
        Assert.Equal(3, calc.BranchesHit);
        Assert.Equal([8], calc.UncoveredLines);
        // 3,000,000,000 hits (gcovr's 64-bit counters) saturate to int.MaxValue, never wrap to
        // a negative that would flip a covered line to a miss.
        Assert.Equal(int.MaxValue, calc.LineHits[5]);
        Assert.Empty(report.Warnings);
        Assert.Equal(["/home/runner/work/myproj/myproj"], report.SourceRoots);
    }

    // ── coverage.py (Python) ──────────────────────────────────────────────────

    [Fact]
    public void CoveragePy_TwoModules_AggregateSemanticsMatchHandCount()
    {
        // module.py: lines 1-5,7,8 hits 1,1,1,1,0,1,0 → 5/7; branches 3:(1/2) + 7:(2/2) → 3/4.
        // __init__.py: 1/1. Report totals: 6/8 lines = 0.75, 3/4 branches.
        var report = CoberturaParser.ParsePath($"{Corpus}/coveragepy/coverage.xml");

        Assert.Equal(2, report.Files.Count);
        var module = report.Files.Single(f => f.Path == "/home/runner/work/myproj/myproj/src/mypkg/module.py");
        Assert.Equal(7, module.LinesTotal);
        Assert.Equal(5, module.LinesHit);
        Assert.Equal(4, module.BranchesTotal);
        Assert.Equal(3, module.BranchesHit);

        var init = report.Files.Single(f => f.Path == "/home/runner/work/myproj/myproj/src/mypkg/__init__.py");
        Assert.Equal(1, init.LinesTotal);
        Assert.Equal(1, init.LinesHit);
        Assert.False(init.HasBranchData);

        Assert.Equal(0.75, report.LineRate);
        Assert.Equal(0.75, report.BranchRate);
        Assert.Empty(report.Warnings);
    }

    // ── cover2cover (JaCoCo → Cobertura, Java) ────────────────────────────────

    [Fact]
    public void Cover2Cover_RelativeSourceRoot_PrefixesKeyAndKeepsJacocoBranchTotals()
    {
        // Lines 10,12,13,15,17 hits 1,1,1,1,0 → 4/5; branches 12:(1/3) + 15:(1/2) → 2/5.
        // The relative root `src/main/java` still prefixes the key — identity must be stable
        // whether or not the root happens to be absolute.
        var report = CoberturaParser.ParsePath($"{Corpus}/cover2cover/coverage.xml");

        var foo = Assert.Single(report.Files);
        Assert.Equal("src/main/java/com/example/Foo.java", foo.Path);
        Assert.Equal(5, foo.LinesTotal);
        Assert.Equal(4, foo.LinesHit);
        Assert.Equal(5, foo.BranchesTotal);
        Assert.Equal(2, foo.BranchesHit);
        Assert.Equal(0.4, report.BranchRate);
        Assert.Empty(report.Warnings);
    }

    // ── grcov (Rust) ──────────────────────────────────────────────────────────

    [Fact]
    public void Grcov_NoOpSourceRootAndCountValuedConditions_KeepsLineAggregate()
    {
        // Lines 3,5,7,9,11 hits 1,7,7,2,0 → 4/5; branches 7:(1/2) + 9:(0/0) → 1/2.
        var report = CoberturaParser.ParsePath($"{Corpus}/grcov/coverage.xml");

        var main = Assert.Single(report.Files);
        // <source>.</source> is a no-op root: no prefix, no declared root on the report.
        Assert.Equal("src/main.rs", main.Path);
        Assert.Empty(report.SourceRoots);
        Assert.Equal(5, main.LinesTotal);
        Assert.Equal(4, main.LinesHit);
        Assert.Equal(2, main.BranchesTotal);
        Assert.Equal(1, main.BranchesHit);
        // grcov writes <condition coverage="1"/> as a COUNT, not a percentage; read as 1% both
        // conditions derive 0 covered, so the 2-outcome consistency gate (2 conditions × 2 ≠
        // line total 2) must drop the per-condition detail and keep the honest 1/2 aggregate.
        Assert.False(main.ConditionsByLine.ContainsKey(7));
        Assert.Equal(0.5, report.BranchRate);
        Assert.Empty(report.Warnings);
    }

    // ── ReportGenerator (merged .NET output) ──────────────────────────────────

    [Fact]
    public void ReportGenerator_ComplexityNaN_ParsesWithPerMethodLinePartitioning()
    {
        // Add: 10,11 hits 4,4; Div: 20,21,22,24 hits 2,2,1,0 → 5/6 lines.
        // Branches 11:(2/2) + 21:(1/3) → 3/5. complexity="NaN" must not disturb parsing.
        var report = CoberturaParser.ParsePath($"{Corpus}/reportgenerator/Cobertura.xml");

        var calc = Assert.Single(report.Files);
        Assert.Equal("/home/runner/work/app/src/MyApp/Calculator.cs", calc.Path);
        Assert.Equal(6, calc.LinesTotal);
        Assert.Equal(5, calc.LinesHit);
        Assert.Equal(5, calc.BranchesTotal);
        Assert.Equal(3, calc.BranchesHit);
        Assert.Equal([24], calc.UncoveredLines);
        Assert.Empty(report.Warnings);
    }

    // ── Reference Cobertura (coverage-04.dtd example) ─────────────────────────

    [Fact]
    public void ReferenceCobertura_DoctypeAndDriveLetterRoot_ParsesCanonicalShape()
    {
        // The canonical coverage-04.dtd document: DOCTYPE must be skipped (not rejected — the
        // format's own emitters write it), and the Windows drive-letter root must prefix the key.
        // Lines 12,13,16,17,19,24 hits 3,19,16,9,7,0 → 5/6; branches 13:(2/2) + 16:(1/2) → 3/4.
        var report = CoberturaParser.ParsePath($"{Corpus}/reference/cobertura-dtd-example.xml");

        var search = Assert.Single(report.Files);
        Assert.Equal("C:/local/mvn-project/src/main/java/search/BinarySearch.java", search.Path);
        Assert.Equal(6, search.LinesTotal);
        Assert.Equal(5, search.LinesHit);
        Assert.Equal(4, search.BranchesTotal);
        Assert.Equal(3, search.BranchesHit);
        Assert.Equal(5.0 / 6, report.LineRate);
        Assert.Equal(0.75, report.BranchRate);
        Assert.Empty(report.Warnings);
    }

    // ── Monorepo: same relative name, genuinely different files ───────────────

    [Fact]
    public void Monorepo_SameRelativeNameUnderDifferentRoots_StaysTwoRootedFiles()
    {
        // svc-a app/main.py 8/10 and svc-b app/main.py 2/6 are DIFFERENT files. Rooting each
        // key against its report's <source> keeps them distinct: 10/16 lines = 62.5%, never
        // the silently-fused 8/10 that discarding the roots produced.
        var report = CoberturaParser.ParsePath($"{Corpus}/monorepo");

        Assert.Equal(2, report.Files.Count);
        var svcA = report.Files.Single(f => f.Path == "/home/runner/work/mono/mono/services/svc-a/app/main.py");
        Assert.Equal(10, svcA.LinesTotal);
        Assert.Equal(8, svcA.LinesHit);
        var svcB = report.Files.Single(f => f.Path == "/home/runner/work/mono/mono/services/svc-b/app/main.py");
        Assert.Equal(6, svcB.LinesTotal);
        Assert.Equal(2, svcB.LinesHit);

        Assert.Equal(16, report.TotalLines);
        Assert.Equal(10, report.TotalLinesHit);
        Assert.Equal(0.625, report.LineRate);
        // Different roots + shared file name: the merge cannot prove these are distinct files
        // (it cannot probe the producing disk), so the honest cross-root ambiguity warning
        // fires while both entries are kept.
        Assert.Contains(report.Warnings, w => w.Kind == CoverageWarningKind.FileIdentityAmbiguous);
    }

    // ── Path identity: the same file under two Coverlet conventions ───────────

    [Fact]
    public void PathIdentity_DefaultVsDeterministicSourcePaths_MergesWithAmbiguityWarning()
    {
        // The SAME Calculator.cs uploaded under Coverlet's default convention (root `/` +
        // machine-absolute filename) and DeterministicSourcePaths (root `/_/` + repo-relative
        // filename). No root arithmetic can unify the keys, so the merge keeps both entries
        // (totals double-count: 4/6 lines, 2/4 branches) and MUST surface the ambiguity.
        var report = CoberturaParser.ParsePath($"{Corpus}/pathidentity");

        Assert.Equal(2, report.Files.Count);
        Assert.Single(report.Files, f => f.Path == "/home/runner/work/app/app/src/MyApp/Calculator.cs");
        Assert.Single(report.Files, f => f.Path == "/_/src/MyApp/Calculator.cs");
        Assert.Equal(6, report.TotalLines);
        Assert.Equal(4, report.TotalLinesHit);
        Assert.Equal(4, report.TotalBranches);
        Assert.Equal(2, report.TotalBranchesHit);

        var warning = Assert.Single(report.Warnings, w => w.Kind == CoverageWarningKind.FileIdentityAmbiguous);
        Assert.Contains("/home/runner/work/app/app/src/MyApp/Calculator.cs", warning.Detail);
        Assert.Contains("/_/src/MyApp/Calculator.cs", warning.Detail);
    }

    // ── Edge: empty packages ──────────────────────────────────────────────────

    [Fact]
    public void EmptyPackages_NothingMeasured_IsNoDataNotFullCoverage()
    {
        var report = CoberturaParser.ParsePath($"{Corpus}/edge/empty-packages.xml");

        Assert.Empty(report.Files);
        Assert.Null(report.LineRate);
        Assert.False(report.HasLineData);
        // "We measured nothing" must gate as NoData, never as a passing 100%.
        Assert.Equal(GateOutcome.NoData, report.Evaluate(80).Outcome);
    }

    // ── Edge: case-differing filenames are distinct files ─────────────────────

    [Fact]
    public void CaseSensitivePair_StaysTwoFilesAtFiftyPercent()
    {
        // linux/net/netfilter really contains both xt_TCPMSS.c (4/4) and xt_tcpmss.c (0/4).
        // Ordinal keying keeps them apart: 4/8 = 50%, not a case-fused 4/4 that erases the
        // uncovered file's misses.
        var report = CoberturaParser.ParsePath($"{Corpus}/edge/gcovr-case-sensitive.xml");

        Assert.Equal(2, report.Files.Count);
        var upper = report.Files.Single(f => f.Path == "/home/runner/linux/net/netfilter/xt_TCPMSS.c");
        Assert.Equal(4, upper.LinesHit);
        var lower = report.Files.Single(f => f.Path == "/home/runner/linux/net/netfilter/xt_tcpmss.c");
        Assert.Equal(0, lower.LinesHit);
        Assert.Equal(0.5, report.LineRate);
    }

    // ── Edge: non-default file names need --pattern ───────────────────────────

    [Fact]
    public void NamedDir_DefaultPattern_MatchesNothing()
    {
        // gcovr writes coverage.xml, `coverage xml` writes what you tell it: neither matches
        // the default `**/coverage.cobertura.xml` glob — the reason the pattern is settable.
        var report = CoberturaParser.ParsePath($"{Corpus}/edge/gcovr-named-dir");

        Assert.Empty(report.Files);
    }

    [Fact]
    public void NamedDir_ExplicitPatterns_ParseEachProducersFile()
    {
        // coverage.xml is the gcovr sample (4/5 lines, 3/4 branches); Cobertura.xml is the
        // coverage.py sample (6/8 lines, 3/4 branches). Each pattern selects exactly one.
        var gcovr = CoberturaParser.ParseDirectory($"{Corpus}/edge/gcovr-named-dir", "coverage.xml");
        Assert.Equal(5, gcovr.TotalLines);
        Assert.Equal(4, gcovr.TotalLinesHit);
        Assert.Equal(3, gcovr.TotalBranchesHit);

        var coveragePy = CoberturaParser.ParseDirectory($"{Corpus}/edge/gcovr-named-dir", "cobertura.xml");
        Assert.Equal(8, coveragePy.TotalLines);
        Assert.Equal(6, coveragePy.TotalLinesHit);
        Assert.Equal(4, coveragePy.TotalBranches);
    }

    // ── Whole-corpus sweep ────────────────────────────────────────────────────

    [Fact]
    public void EveryCorpusSample_ParsesWithoutThrowing()
    {
        var samples = Directory.GetFiles(Corpus, "*.xml", SearchOption.AllDirectories);

        Assert.Equal(14, samples.Length);
        foreach (var sample in samples)
        {
            var report = CoberturaParser.ParseFile(sample);
            Assert.NotNull(report);
        }
    }
}
