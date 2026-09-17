using TUnit.Assertions.Enums;
using System.Text;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

/// <summary>
/// File identity across path conventions: <c>&lt;source&gt;</c>-root resolution of relative
/// class filenames, Ordinal (case-sensitive) keying with drive-letter normalization, the
/// <see cref="CoverageWarningKind.FileIdentityAmbiguous"/> surfacing of cross-convention
/// merges, and the diff's unique-file-name pairing fallback. Repro shapes come from real
/// emitters: coverage.py monorepo services, Coverlet default vs DeterministicSourcePaths,
/// gcovr on the Linux kernel tree.
/// </summary>
public sealed class CorePathIdentityTests
{
    private static CoverageReport ParseXml(string xml) =>
        CoberturaParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

    /// <summary>One-class document with optional <c>&lt;source&gt;</c> roots.</summary>
    private static string Doc(
        string[] roots,
        string filename,
        string lines = """<line number="1" hits="1" branch="false" />""")
    {
        var sources = roots.Length is 0
            ? ""
            : "<sources>" + string.Concat(roots.Select(static r => $"<source>{r}</source>")) + "</sources>";
        return $"""
                <?xml version="1.0"?>
                <coverage>{sources}<packages><package><classes>
                  <class name="X" filename="{filename}"><lines>{lines}</lines></class>
                </classes></package></packages></coverage>
                """;
    }

    // ── <source>-root resolution (C1 / #15) ──

    [Test]
    public async Task Parse_RelativeFilename_ResolvesAgainstSourceRoot()
    {
        var report = ParseXml(Doc(["/home/runner/work/mono/mono/services/svc-a"], "app/main.py"));

        await Assert.That(report.Files.Single().Path).IsEqualTo("/home/runner/work/mono/mono/services/svc-a/app/main.py");
        await Assert.That(report.SourceRoots.Single()).IsEqualTo("/home/runner/work/mono/mono/services/svc-a");
        await Assert.That(report.Warnings).IsEmpty();
    }

    [Test]
    public async Task Parse_TrailingSlashRoot_JoinsWithoutDoubleSlash()
    {
        // DeterministicSourcePaths emits <source>/_/</source> with repo-relative filenames.
        var report = ParseXml(Doc(["/_/"], "src/MyApp/Calculator.cs"));

        await Assert.That(report.Files.Single().Path).IsEqualTo("/_/src/MyApp/Calculator.cs");
    }

    [Test]
    public async Task Parse_RelativeRoot_IsPrependedToo()
    {
        // cover2cover emits relative roots like src/main/java with package-relative filenames;
        // the joined key is what distinguishes two modules' com/example/Foo.java from each other.
        var report = ParseXml(Doc(["src/main/java"], "com/example/Foo.java"));

        await Assert.That(report.Files.Single().Path).IsEqualTo("src/main/java/com/example/Foo.java");
    }

    [Test]
    [Arguments("/abs/path/F.cs", "/abs/path/F.cs")]
    [Arguments("C:/proj/src/F.cs", "C:/proj/src/F.cs")]
    public async Task Parse_AlreadyRootedFilename_IsNotPrefixed(string filename, string expected)
    {
        // The rooted check is manual (leading '/' or drive-letter prefix): Path.IsPathRooted
        // says "C:/x" is NOT rooted on Linux, and reports cross machines — a Windows-emitted
        // report analyzed in a Linux CI job must not get a root prepended onto C:/.
        var report = ParseXml(Doc(["/some/root"], filename));

        await Assert.That(report.Files.Single().Path).IsEqualTo(expected);
    }

    [Test]
    [Arguments(".")]
    [Arguments("./")]
    public async Task Parse_DotSourceRoot_IsANoOp(string root)
    {
        // grcov emits <source>.</source>; prepending "." would change every key while adding
        // no identity information.
        var report = ParseXml(Doc([root], "src/main.rs"));

        await Assert.That(report.Files.Single().Path).IsEqualTo("src/main.rs");
        await Assert.That(report.SourceRoots).IsEmpty();
    }

    [Test]
    public async Task Parse_BackslashSourceRoot_IsSeparatorNormalizedBeforeJoining()
    {
        var report = ParseXml(Doc([@"C:\proj"], @"src\A.cs"));

        await Assert.That(report.Files.Single().Path).IsEqualTo("C:/proj/src/A.cs");
    }

    [Test]
    public async Task Parse_NoOpRootAlongsideRealRoot_FirstDeclaredStillWins_AndWarns()
    {
        // coverage.py shape: <source>.</source> (the project dir) alongside a site-packages
        // root. The no-op is still the FIRST declared root, so relative filenames stay
        // unprefixed — resolving them against the later real root would rewrite every key
        // against the documented first-wins contract — and the guaranteed multi-root
        // ambiguity warning fires: two declared conventions, files not attributable to a
        // unique root. The no-op keeps its slot in SourceRoots (as "") so a merge can tell
        // this report apart from one that declared only the real root.
        var report = ParseXml(Doc([".", "/usr/lib/python3/dist-packages"], "app/main.py"));

        await Assert.That(report.Files.Single().Path).IsEqualTo("app/main.py");
        await Assert.That(report.SourceRoots).IsEquivalentTo(["", "/usr/lib/python3/dist-packages"], CollectionOrdering.Matching);
        var w = await Assert.That(report.Warnings).HasSingleItem();
        await Assert.That(w.Kind).IsEqualTo(CoverageWarningKind.FileIdentityAmbiguous);
        await Assert.That(w.Detail).Contains("unprefixed");
    }

    [Test]
    public async Task Parse_RealRootThenNoOpRoot_ResolvesAgainstTheRealFirst_AndWarns()
    {
        // Declaration order decides: with the real root first, relative filenames prefix
        // against it; the trailing no-op still counts as a second convention and warns.
        var report = ParseXml(Doc(["/repo", "."], "app/main.py"));

        await Assert.That(report.Files.Single().Path).IsEqualTo("/repo/app/main.py");
        await Assert.That(report.SourceRoots).IsEquivalentTo(["/repo", ""], CollectionOrdering.Matching);
        var w = await Assert.That(report.Warnings).HasSingleItem();
        await Assert.That(w.Kind).IsEqualTo(CoverageWarningKind.FileIdentityAmbiguous);
        await Assert.That(w.Detail).Contains("'/repo'");
    }

    [Test]
    public async Task Parse_RepeatedNoOpRoots_AreOneEffectiveRoot_NoWarning()
    {
        // "." and "./" spell the same no-op; both resolve identically, so there is no
        // identity ambiguity to warn about — and the report still declares no roots,
        // byte-identical to the lone-"." behavior.
        var report = ParseXml(Doc([".", "./"], "src/main.rs"));

        await Assert.That(report.Files.Single().Path).IsEqualTo("src/main.rs");
        await Assert.That(report.SourceRoots).IsEmpty();
        await Assert.That(report.Warnings).IsEmpty();
    }

    [Test]
    public async Task Parse_DuplicateIdenticalRoots_DeduplicateWithoutWarning()
    {
        // ReportGenerator's merged output repeats the same <source> once per input report:
        // one distinct root, no multi-root ambiguity — and the deduplicated list keeps the
        // merge fast path against a single-root sibling.
        var report = ParseXml(Doc(["/repo", "/repo"], "src/A.cs"));

        await Assert.That(report.Files.Single().Path).IsEqualTo("/repo/src/A.cs");
        await Assert.That(report.SourceRoots).IsEquivalentTo(["/repo"], CollectionOrdering.Matching);
        await Assert.That(report.Warnings).IsEmpty();

        var sibling = ParseXml(Doc(["/repo"], "src/B.cs"));
        await Assert.That(CoverageReport.Merge(report, sibling).Warnings).IsEmpty();
    }

    [Test]
    public async Task Parse_MultipleSourceRoots_FirstWinsDeterministically_AndWarns()
    {
        // The analyzing machine cannot probe the disk the report came from, so with several
        // roots the first is chosen deterministically and the ambiguity is surfaced.
        var report = ParseXml(Doc(["/first", "/second"], "app/f.cs"));

        await Assert.That(report.Files.Single().Path).IsEqualTo("/first/app/f.cs");
        await Assert.That(report.SourceRoots).IsEquivalentTo(["/first", "/second"], CollectionOrdering.Matching);
        var w = await Assert.That(report.Warnings).HasSingleItem();
        await Assert.That(w.Kind).IsEqualTo(CoverageWarningKind.FileIdentityAmbiguous);
        await Assert.That(w.Detail).Contains("/first");
    }

    [Test]
    public async Task ParseAsync_ResolvesSourceRoots_LikeSync()
    {
        using var stream = new MemoryStream(
            Encoding.UTF8.GetBytes(Doc(["/repo/svc-a"], "app/main.py")));
        var report = await CoberturaParser.ParseAsync(stream);

        await Assert.That(report.Files.Single().Path).IsEqualTo("/repo/svc-a/app/main.py");
        await Assert.That(report.SourceRoots).IsEquivalentTo(["/repo/svc-a"], CollectionOrdering.Matching);
    }

    // ── The monorepo fusion bug (#15): distinct files sharing a relative name ──

    private static string SvcDoc(string svc, string lines) =>
        Doc([$"/home/runner/work/mono/mono/services/{svc}"], "app/main.py", lines);

    private static string HitLines(int hit, int missed)
    {
        var sb = new StringBuilder();
        var n = 1;
        for (var i = 0; i < hit; i++) sb.Append($"""<line number="{n++}" hits="1" branch="false" />""");
        for (var i = 0; i < missed; i++) sb.Append($"""<line number="{n++}" hits="0" branch="false" />""");
        return sb.ToString();
    }

    [Test]
    public async Task Merge_SameRelativeNameUnderDifferentRoots_StaysTwoDistinctFiles()
    {
        // Two DIFFERENT coverage.py files, both named app/main.py, under svc-a and svc-b.
        // Pre-fix these fused into one entry via Math.Max: 8/10 = 80% reported, svc-b's four
        // uncovered lines vanished, and a --min-line 70 gate passed a repo that is at 62.5%.
        var a = ParseXml(SvcDoc("svc-a", HitLines(hit: 8, missed: 2)));
        var b = ParseXml(SvcDoc("svc-b", HitLines(hit: 2, missed: 4)));

        var merged = CoverageReport.Merge(a, b);

        await Assert.That(merged.Files.Count).IsEqualTo(2);
        await Assert.That(merged.TotalLines).IsEqualTo(16);
        await Assert.That(merged.TotalLinesHit).IsEqualTo(10);
        await Assert.That(merged.LineRate).IsEqualTo(0.625);
        await Assert.That(merged.Evaluate(70).Outcome).IsEqualTo(GateOutcome.Fail);

        // Roots union onto the merged report so a later fold still compares conventions.
        await Assert.That(merged.SourceRoots.Count).IsEqualTo(2);
    }

    // ── The two-convention double-count (#14): same file, different path conventions ──

    private static string CoverletDefaultDoc() =>
        Doc(["/"], "home/runner/work/app/app/src/MyApp/Calculator.cs",
            """<line number="10" hits="1" branch="false" /><line number="11" hits="1" branch="false" /><line number="12" hits="0" branch="false" />""");

    private static string DeterministicDoc() =>
        Doc(["/_/"], "src/MyApp/Calculator.cs",
            """<line number="10" hits="1" branch="false" /><line number="11" hits="1" branch="false" /><line number="12" hits="0" branch="false" />""");

    [Test]
    public async Task Merge_SameFileUnderTwoPathConventions_KeepsBothEntries_ButWarnsAmbiguousIdentity()
    {
        // Coverlet default (<source>/</source> + machine-absolute filename) vs
        // DeterministicSourcePaths (<source>/_/</source> + repo-relative filename) for the
        // SAME source file. No root arithmetic can unify '/home/runner/.../Calculator.cs'
        // with '/_/src/MyApp/Calculator.cs' without probing a disk this machine may not
        // have, so the double-count stays — but it must be OBSERVABLE, not silent.
        var merged = CoverageReport.Merge(ParseXml(CoverletDefaultDoc()), ParseXml(DeterministicDoc()));

        await Assert.That(merged.Files.Count).IsEqualTo(2);   // honest: still double-counted
        var w = await Assert.That(merged.Warnings).HasSingleItem();
        await Assert.That(w.Kind).IsEqualTo(CoverageWarningKind.FileIdentityAmbiguous);
        await Assert.That(w.Detail).Contains("/home/runner/work/app/app/src/MyApp/Calculator.cs");
        await Assert.That(w.Detail).Contains("/_/src/MyApp/Calculator.cs");
    }

    [Test]
    public async Task Merge_SameRootsOnBothSides_NeverScansForAmbiguity()
    {
        // Partitioned test runs from the same pipeline (same roots) routinely cover disjoint
        // same-named files — that is not ambiguity, and must produce zero warning noise.
        var a = ParseXml(Doc(["/"], "home/ci/src/A/Util.cs"));
        var b = ParseXml(Doc(["/"], "home/ci/src/B/Util.cs"));

        var merged = CoverageReport.Merge(a, b);

        await Assert.That(merged.Files.Count).IsEqualTo(2);
        await Assert.That(merged.Warnings).IsEmpty();
    }

    [Test]
    public async Task Merge_HandBuiltReportsWithoutRoots_NeverScansForAmbiguity()
    {
        var a = new CoverageReport([new FileCoverage("x/Program.cs", 1, 2, 0, 0)]);
        var b = new CoverageReport([new FileCoverage("y/Program.cs", 2, 2, 0, 0)]);

        await Assert.That(CoverageReport.Merge(a, b).Warnings).IsEmpty();
    }

    [Test]
    public async Task Merge_RootSpellingVariants_TakeTheSameRootsFastPath()
    {
        // Drive-letter case and a trailing slash are spellings, not identities: partitioned
        // Windows CI jobs (c:\agent\work\repo vs C:/agent/work/repo/) key their files under
        // one normalized root, so the merge must treat them as the same-pipeline case —
        // zero cross-convention warnings for genuinely distinct same-named files.
        var a = ParseXml(Doc([@"c:\agent\work\repo"], "src/A/Util.cs"));
        var b = ParseXml(Doc(["C:/agent/work/repo/"], "src/B/Util.cs"));

        var merged = CoverageReport.Merge(a, b);

        await Assert.That(merged.Files.Count).IsEqualTo(2);
        await Assert.That(merged.Warnings).IsEmpty();
        await Assert.That(merged.SourceRoots).IsEquivalentTo(["C:/agent/work/repo"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Merge_HandBuiltRootSpellingVariants_CompareByNormalizedIdentity()
    {
        // Programmatically constructed reports never pass through the parser's root
        // normalization; the merge-side comparison must normalize for itself rather than
        // read raw spelling as a convention change.
        var a = new CoverageReport([new FileCoverage("x/Util.cs", 1, 2, 0, 0)]) { SourceRoots = [@"c:\repo"] };
        var b = new CoverageReport([new FileCoverage("y/Util.cs", 2, 2, 0, 0)]) { SourceRoots = ["C:/repo/"] };

        await Assert.That(CoverageReport.Merge(a, b).Warnings).IsEmpty();
    }

    // ── Diff: unique-file-name pairing fallback (C1) and the migration effect ──

    [Test]
    public async Task Diff_SameFileUnderTwoPathConventions_ReadsUnchanged_NotRemovedPlusAdded()
    {
        // before = Coverlet default convention, after = DeterministicSourcePaths. Identical
        // coverage of the same file must diff as Unchanged via the unique-file-name pairing,
        // not as a -66.67 removal plus a +66.67 addition.
        var result = CoverageDiff.Compare(ParseXml(CoverletDefaultDoc()), ParseXml(DeterministicDoc()));

        var d = await Assert.That(result.Files).HasSingleItem();
        await Assert.That(d.Change).IsEqualTo(FileChangeKind.Unchanged);
        await Assert.That(d.Path).IsEqualTo("/_/src/MyApp/Calculator.cs");   // reported under the After identity
        await Assert.That(result.Added).IsEmpty();
        await Assert.That(result.Removed).IsEmpty();
    }

    [Test]
    public async Task Diff_PreSourceRootSnapshotAgainstResolvedReport_PairsAsTheSameFile()
    {
        // The migration effect of source-root resolution: FileCoverage.Path values change
        // (e.g. 'src/MyApp/Calculator.cs' → '/_/src/MyApp/Calculator.cs'), so a snapshot
        // taken before this fix carries the old keys. Diffing it against a post-fix report
        // must pair the identities through the file-name fallback instead of reporting the
        // whole codebase as removed+added.
        var preFixSnapshot = new CoverageReport([new FileCoverage("src/MyApp/Calculator.cs", 1, 3, 0, 0)]);
        var postFix = ParseXml(DeterministicDoc());   // 2/3 covered

        var result = CoverageDiff.Compare(preFixSnapshot, postFix);

        var d = await Assert.That(result.Files).HasSingleItem();
        await Assert.That(d.Change).IsEqualTo(FileChangeKind.Modified);
        await Assert.That(d.Before).IsNotNull();
        await Assert.That(d.After).IsNotNull();
        await Assert.That(d.Before!.Value).IsEqualTo(1.0 / 3).Within(1e-10);
        await Assert.That(d.After!.Value).IsEqualTo(2.0 / 3).Within(1e-10);
        await Assert.That(result.Added).IsEmpty();
        await Assert.That(result.Removed).IsEmpty();
    }

    [Test]
    public async Task Diff_AmbiguousFileNameTail_StaysRemovedPlusAdded()
    {
        // Two leftover Before files share the name main.py: pairing either with the single
        // leftover After file would be a guess (and would re-fuse the monorepo shape), so
        // nothing pairs.
        var before = new CoverageReport([
            new FileCoverage("svc-a/app/main.py", 8, 10, 0, 0),
            new FileCoverage("svc-b/app/main.py", 2, 6, 0, 0)
        ]);
        var after = new CoverageReport([new FileCoverage("/repo/services/svc-a/app/main.py", 8, 10, 0, 0)]);

        var result = CoverageDiff.Compare(before, after);

        await Assert.That(result.Removed.Count()).IsEqualTo(2);
        await Assert.That(result.Added).HasSingleItem();
    }

    [Test]
    public async Task Diff_FileNameTailMatchesOnWholeSegmentsOnly()
    {
        // 'MyCalculator.cs' ends with the raw string "Calculator.cs" but is a different file
        // name — the fallback compares whole final path segments, so no pairing happens.
        var before = new CoverageReport([new FileCoverage("src/MyCalculator.cs", 1, 2, 0, 0)]);
        var after = new CoverageReport([new FileCoverage("src/lib/Calculator.cs", 2, 2, 0, 0)]);

        var result = CoverageDiff.Compare(before, after);

        await Assert.That(result.Removed).HasSingleItem();
        await Assert.That(result.Added).HasSingleItem();
    }

    // ── Ordinal keying with normalized keys (C2) ──

    [Test]
    public async Task Merge_CaseDistinctFilenames_StayDistinctAcrossReports()
    {
        // gcovr shape: xt_TCPMSS.c (fully hit) and xt_tcpmss.c (untouched) coexist in
        // linux/net/netfilter. Cross-report merge must not fuse them either.
        var a = Cobertura.NewDoc()
            .AddClass("net/netfilter/xt_TCPMSS.c", c => c.Line(1, hits: 1))
            .Parse();
        var b = Cobertura.NewDoc()
            .AddClass("net/netfilter/xt_tcpmss.c", c => c.Line(1, hits: 0))
            .Parse();

        var merged = CoverageReport.Merge(a, b);

        await Assert.That(merged.Files.Count).IsEqualTo(2);
        await Assert.That(merged.LineRate).IsEqualTo(0.5);
    }

    [Test]
    public async Task Parse_LowercaseDriveLetter_NormalizesToUppercaseKey()
    {
        // Windows toolchains disagree on drive-letter casing; Ordinal keying gets its
        // Windows cross-report stability from normalizing the KEY, not from a
        // case-insensitive comparer (a Dictionary has one comparer for every key).
        var report = ParseXml(Doc([], "c:/proj/src/A.cs"));

        await Assert.That(report.Files.Single().Path).IsEqualTo("C:/proj/src/A.cs");
    }

    [Test]
    public async Task Merge_DriveLetterCaseAndSeparatorVariants_UnionAsOneFile()
    {
        var a = ParseXml(Doc([], @"c:\proj\src\A.cs"));
        var b = ParseXml(Doc([], "C:/proj/src/A.cs"));

        var merged = CoverageReport.Merge(a, b);

        await Assert.That(merged.Files.Single().Path).IsEqualTo("C:/proj/src/A.cs");
    }

    [Test]
    public async Task Exclude_PreservesSourceRoots()
    {
        var report = ParseXml(Doc(["/repo"], "src/A.cs"));

        var filtered = report.Exclude(["nothing-matches"]);

        await Assert.That(filtered.SourceRoots).IsEquivalentTo(["/repo"], CollectionOrdering.Matching);
    }

    // ── run-004 regression pins ──

    [Test]
    public async Task Merge_HandBuiltUnnormalizedRoots_DedupesByNormalizedIdentity()
    {
        // Parser-path roots are already normalized; hand-built reports are not. A raw-spelling
        // union kept both c:\repo and C:/repo/, so every later merge saw "different" roots
        // and re-flagged ambiguity forever.
        var a = new CoverageReport([]) { SourceRoots = [@"c:\repo"] };
        var b = new CoverageReport([]) { SourceRoots = ["C:/repo/"] };

        var once = CoverageReport.Merge(a, b);

        await Assert.That(once.SourceRoots).HasSingleItem();

        var again = CoverageReport.Merge(once, b);
        await Assert.That(again.SourceRoots).HasSingleItem();
    }
}