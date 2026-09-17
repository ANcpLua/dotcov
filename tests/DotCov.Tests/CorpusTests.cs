using TUnit.Assertions.Enums;

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

    [Test]
    public async Task Gcovr_SingleQuotedDeclAndDoctype_ParsesWithRootedKeyAndSaturatedHits()
    {
        // Lines 5,7,8,10,12 with hits 3e9,3e9,0,2,2 → 4/5 hit; branches 7:(1/2) + 12:(2/2) → 3/4.
        // The relative filename `src/calc.c` roots against the single <source> element.
        var report = CoberturaParser.Parse(ReportResolver.Resolve($"{Corpus}/gcovr/coverage.xml"));

        var calc = await Assert.That(report.Files).HasSingleItem();
        await Assert.That(calc.Path).IsEqualTo("/home/runner/work/myproj/myproj/src/calc.c");
        await Assert.That(calc.LinesTotal).IsEqualTo(5);
        await Assert.That(calc.LinesHit).IsEqualTo(4);
        await Assert.That(calc.BranchesTotal).IsEqualTo(4);
        await Assert.That(calc.BranchesHit).IsEqualTo(3);
        await Assert.That(calc.UncoveredLines).IsEquivalentTo([8], CollectionOrdering.Matching);
        // 3,000,000,000 hits (gcovr's 64-bit counters) saturate to int.MaxValue, never wrap to
        // a negative that would flip a covered line to a miss.
        await Assert.That(calc.LineHits[5]).IsEqualTo(int.MaxValue);
        await Assert.That(report.Warnings).IsEmpty();
        await Assert.That(report.SourceRoots).IsEquivalentTo(["/home/runner/work/myproj/myproj"], CollectionOrdering.Matching);
    }

    // ── coverage.py (Python) ──────────────────────────────────────────────────

    [Test]
    public async Task CoveragePy_TwoModules_AggregateSemanticsMatchHandCount()
    {
        // module.py: lines 1-5,7,8 hits 1,1,1,1,0,1,0 → 5/7; branches 3:(1/2) + 7:(2/2) → 3/4.
        // __init__.py: 1/1. Report totals: 6/8 lines = 0.75, 3/4 branches.
        var report = CoberturaParser.Parse(ReportResolver.Resolve($"{Corpus}/coveragepy/coverage.xml"));

        await Assert.That(report.Files.Count).IsEqualTo(2);
        var module = report.Files.Single(f => f.Path == "/home/runner/work/myproj/myproj/src/mypkg/module.py");
        await Assert.That(module.LinesTotal).IsEqualTo(7);
        await Assert.That(module.LinesHit).IsEqualTo(5);
        await Assert.That(module.BranchesTotal).IsEqualTo(4);
        await Assert.That(module.BranchesHit).IsEqualTo(3);

        var init = report.Files.Single(f => f.Path == "/home/runner/work/myproj/myproj/src/mypkg/__init__.py");
        await Assert.That(init.LinesTotal).IsEqualTo(1);
        await Assert.That(init.LinesHit).IsEqualTo(1);
        await Assert.That(init.HasBranchData).IsFalse();

        await Assert.That(report.LineRate).IsEqualTo(0.75);
        await Assert.That(report.BranchRate).IsEqualTo(0.75);
        await Assert.That(report.Warnings).IsEmpty();
    }

    // ── cover2cover (JaCoCo → Cobertura, Java) ────────────────────────────────

    [Test]
    public async Task Cover2Cover_RelativeSourceRoot_PrefixesKeyAndKeepsJacocoBranchTotals()
    {
        // Lines 10,12,13,15,17 hits 1,1,1,1,0 → 4/5; branches 12:(1/3) + 15:(1/2) → 2/5.
        // The relative root `src/main/java` still prefixes the key — identity must be stable
        // whether or not the root happens to be absolute.
        var report = CoberturaParser.Parse(ReportResolver.Resolve($"{Corpus}/cover2cover/coverage.xml"));

        var foo = await Assert.That(report.Files).HasSingleItem();
        await Assert.That(foo.Path).IsEqualTo("src/main/java/com/example/Foo.java");
        await Assert.That(foo.LinesTotal).IsEqualTo(5);
        await Assert.That(foo.LinesHit).IsEqualTo(4);
        await Assert.That(foo.BranchesTotal).IsEqualTo(5);
        await Assert.That(foo.BranchesHit).IsEqualTo(2);
        await Assert.That(report.BranchRate).IsEqualTo(0.4);
        await Assert.That(report.Warnings).IsEmpty();
    }

    // ── grcov (Rust) ──────────────────────────────────────────────────────────

    [Test]
    public async Task Grcov_NoOpSourceRootAndCountValuedConditions_KeepsLineAggregate()
    {
        // Lines 3,5,7,9,11 hits 1,7,7,2,0 → 4/5; branches 7:(1/2) + 9:(0/0) → 1/2.
        var report = CoberturaParser.Parse(ReportResolver.Resolve($"{Corpus}/grcov/coverage.xml"));

        var main = await Assert.That(report.Files).HasSingleItem();
        // <source>.</source> is a no-op root: no prefix, no declared root on the report.
        await Assert.That(main.Path).IsEqualTo("src/main.rs");
        await Assert.That(report.SourceRoots).IsEmpty();
        await Assert.That(main.LinesTotal).IsEqualTo(5);
        await Assert.That(main.LinesHit).IsEqualTo(4);
        await Assert.That(main.BranchesTotal).IsEqualTo(2);
        await Assert.That(main.BranchesHit).IsEqualTo(1);
        // grcov writes <condition coverage="1"/> as a COUNT, not a percentage; read as 1% both
        // conditions derive 0 covered, so the 2-outcome consistency gate (2 conditions × 2 ≠
        // line total 2) must drop the per-condition detail and keep the honest 1/2 aggregate.
        await Assert.That(main.ConditionsByLine.ContainsKey(7)).IsFalse();
        await Assert.That(report.BranchRate).IsEqualTo(0.5);
        await Assert.That(report.Warnings).IsEmpty();
    }

    // ── ReportGenerator (merged .NET output) ──────────────────────────────────

    [Test]
    public async Task ReportGenerator_ComplexityNaN_ParsesWithPerMethodLinePartitioning()
    {
        // Add: 10,11 hits 4,4; Div: 20,21,22,24 hits 2,2,1,0 → 5/6 lines.
        // Branches 11:(2/2) + 21:(1/3) → 3/5. complexity="NaN" must not disturb parsing.
        var report = CoberturaParser.Parse(ReportResolver.Resolve($"{Corpus}/reportgenerator/Cobertura.xml"));

        var calc = await Assert.That(report.Files).HasSingleItem();
        await Assert.That(calc.Path).IsEqualTo("/home/runner/work/app/src/MyApp/Calculator.cs");
        await Assert.That(calc.LinesTotal).IsEqualTo(6);
        await Assert.That(calc.LinesHit).IsEqualTo(5);
        await Assert.That(calc.BranchesTotal).IsEqualTo(5);
        await Assert.That(calc.BranchesHit).IsEqualTo(3);
        await Assert.That(calc.UncoveredLines).IsEquivalentTo([24], CollectionOrdering.Matching);
        await Assert.That(report.Warnings).IsEmpty();
    }

    // ── Reference Cobertura (coverage-04.dtd example) ─────────────────────────

    [Test]
    public async Task ReferenceCobertura_DoctypeAndDriveLetterRoot_ParsesCanonicalShape()
    {
        // The canonical coverage-04.dtd document: DOCTYPE must be skipped (not rejected — the
        // format's own emitters write it), and the Windows drive-letter root must prefix the key.
        // Lines 12,13,16,17,19,24 hits 3,19,16,9,7,0 → 5/6; branches 13:(2/2) + 16:(1/2) → 3/4.
        var report = CoberturaParser.Parse(ReportResolver.Resolve($"{Corpus}/reference/cobertura-dtd-example.xml"));

        var search = await Assert.That(report.Files).HasSingleItem();
        await Assert.That(search.Path).IsEqualTo("C:/local/mvn-project/src/main/java/search/BinarySearch.java");
        await Assert.That(search.LinesTotal).IsEqualTo(6);
        await Assert.That(search.LinesHit).IsEqualTo(5);
        await Assert.That(search.BranchesTotal).IsEqualTo(4);
        await Assert.That(search.BranchesHit).IsEqualTo(3);
        await Assert.That(report.LineRate).IsEqualTo(5.0 / 6);
        await Assert.That(report.BranchRate).IsEqualTo(0.75);
        await Assert.That(report.Warnings).IsEmpty();
    }

    // ── Monorepo: same relative name, genuinely different files ───────────────

    [Test]
    public async Task Monorepo_SameRelativeNameUnderDifferentRoots_StaysTwoRootedFiles()
    {
        // svc-a app/main.py 8/10 and svc-b app/main.py 2/6 are DIFFERENT files. Rooting each
        // key against its report's <source> keeps them distinct: 10/16 lines = 62.5%, never
        // the silently-fused 8/10 that discarding the roots produced.
        var report = CoberturaParser.Parse(ReportResolver.Resolve($"{Corpus}/monorepo"));

        await Assert.That(report.Files.Count).IsEqualTo(2);
        var svcA = report.Files.Single(f => f.Path == "/home/runner/work/mono/mono/services/svc-a/app/main.py");
        await Assert.That(svcA.LinesTotal).IsEqualTo(10);
        await Assert.That(svcA.LinesHit).IsEqualTo(8);
        var svcB = report.Files.Single(f => f.Path == "/home/runner/work/mono/mono/services/svc-b/app/main.py");
        await Assert.That(svcB.LinesTotal).IsEqualTo(6);
        await Assert.That(svcB.LinesHit).IsEqualTo(2);

        await Assert.That(report.TotalLines).IsEqualTo(16);
        await Assert.That(report.TotalLinesHit).IsEqualTo(10);
        await Assert.That(report.LineRate).IsEqualTo(0.625);
        // Different roots + shared file name: the merge cannot prove these are distinct files
        // (it cannot probe the producing disk), so the honest cross-root ambiguity warning
        // fires while both entries are kept.
        await Assert.That(report.Warnings).Contains(w => w.Kind == CoverageWarningKind.FileIdentityAmbiguous);
    }

    // ── Path identity: the same file under two Coverlet conventions ───────────

    [Test]
    public async Task PathIdentity_DefaultVsDeterministicSourcePaths_MergesWithAmbiguityWarning()
    {
        // The SAME Calculator.cs uploaded under Coverlet's default convention (root `/` +
        // machine-absolute filename) and DeterministicSourcePaths (root `/_/` + repo-relative
        // filename). No root arithmetic can unify the keys, so the merge keeps both entries
        // (totals double-count: 4/6 lines, 2/4 branches) and MUST surface the ambiguity.
        var report = CoberturaParser.Parse(ReportResolver.Resolve($"{Corpus}/pathidentity"));

        await Assert.That(report.Files.Count).IsEqualTo(2);
        await Assert.That(report.Files.Count(f => f.Path == "/home/runner/work/app/app/src/MyApp/Calculator.cs")).IsEqualTo(1);
        await Assert.That(report.Files.Count(f => f.Path == "/_/src/MyApp/Calculator.cs")).IsEqualTo(1);
        await Assert.That(report.TotalLines).IsEqualTo(6);
        await Assert.That(report.TotalLinesHit).IsEqualTo(4);
        await Assert.That(report.TotalBranches).IsEqualTo(4);
        await Assert.That(report.TotalBranchesHit).IsEqualTo(2);

        var warning = report.Warnings.Single(w => w.Kind == CoverageWarningKind.FileIdentityAmbiguous);
        await Assert.That(warning.Detail).Contains("/home/runner/work/app/app/src/MyApp/Calculator.cs");
        await Assert.That(warning.Detail).Contains("/_/src/MyApp/Calculator.cs");
    }

    // ── Edge: empty packages ──────────────────────────────────────────────────

    [Test]
    public async Task EmptyPackages_NothingMeasured_IsNoDataNotFullCoverage()
    {
        var report = CoberturaParser.Parse(ReportResolver.Resolve($"{Corpus}/edge/empty-packages.xml"));

        await Assert.That(report.Files).IsEmpty();
        await Assert.That(report.LineRate).IsNull();
        await Assert.That(report.HasLineData).IsFalse();
        // "We measured nothing" must gate as NoData, never as a passing 100%.
        await Assert.That(report.Evaluate(80).Outcome).IsEqualTo(GateOutcome.NoData);
    }

    // ── Edge: case-differing filenames are distinct files ─────────────────────

    [Test]
    public async Task CaseSensitivePair_StaysTwoFilesAtFiftyPercent()
    {
        // linux/net/netfilter really contains both xt_TCPMSS.c (4/4) and xt_tcpmss.c (0/4).
        // Ordinal keying keeps them apart: 4/8 = 50%, not a case-fused 4/4 that erases the
        // uncovered file's misses.
        var report = CoberturaParser.Parse(ReportResolver.Resolve($"{Corpus}/edge/gcovr-case-sensitive.xml"));

        await Assert.That(report.Files.Count).IsEqualTo(2);
        var upper = report.Files.Single(f => f.Path == "/home/runner/linux/net/netfilter/xt_TCPMSS.c");
        await Assert.That(upper.LinesHit).IsEqualTo(4);
        var lower = report.Files.Single(f => f.Path == "/home/runner/linux/net/netfilter/xt_tcpmss.c");
        await Assert.That(lower.LinesHit).IsEqualTo(0);
        await Assert.That(report.LineRate).IsEqualTo(0.5);
    }

    // ── Edge: non-default file names need --pattern ───────────────────────────

    [Test]
    public async Task NamedDir_DefaultPattern_MatchesNothing()
    {
        // gcovr writes coverage.xml, `coverage xml` writes what you tell it: neither matches
        // the default `**/coverage.cobertura.xml` glob — the reason the pattern is settable.
        var report = CoberturaParser.Parse(ReportResolver.Resolve($"{Corpus}/edge/gcovr-named-dir"));

        await Assert.That(report.Files).IsEmpty();
    }

    [Test]
    public async Task NamedDir_ExplicitPatterns_ParseEachProducersFile()
    {
        // coverage.xml is the gcovr sample (4/5 lines, 3/4 branches); Cobertura.xml is the
        // coverage.py sample (6/8 lines, 3/4 branches). Each pattern selects exactly one.
        var gcovr = CoberturaParser.Parse(ReportResolver.ResolveDirectory($"{Corpus}/edge/gcovr-named-dir", ReportPattern.Parse("coverage.xml")));
        await Assert.That(gcovr.TotalLines).IsEqualTo(5);
        await Assert.That(gcovr.TotalLinesHit).IsEqualTo(4);
        await Assert.That(gcovr.TotalBranchesHit).IsEqualTo(3);

        var coveragePy = CoberturaParser.Parse(ReportResolver.ResolveDirectory($"{Corpus}/edge/gcovr-named-dir", ReportPattern.Parse("cobertura.xml")));
        await Assert.That(coveragePy.TotalLines).IsEqualTo(8);
        await Assert.That(coveragePy.TotalLinesHit).IsEqualTo(6);
        await Assert.That(coveragePy.TotalBranches).IsEqualTo(4);
    }

    // ── Whole-corpus sweep ────────────────────────────────────────────────────

    [Test]
    public async Task EveryCorpusSample_ParsesWithoutThrowing()
    {
        var samples = Directory.GetFiles(Corpus, "*.xml", SearchOption.AllDirectories);

        await Assert.That(samples.Length).IsEqualTo(14);
        foreach (var sample in samples)
        {
            var report = CoberturaParser.Parse(ReportInput.FromFile(sample));
            await Assert.That(report).IsNotNull();
        }
    }
}