using System.Text;
using DotCov.Tests.Infrastructure;
using Xunit;

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

    [Fact]
    public void Parse_RelativeFilename_ResolvesAgainstSourceRoot()
    {
        var report = ParseXml(Doc(["/home/runner/work/mono/mono/services/svc-a"], "app/main.py"));

        Assert.Equal("/home/runner/work/mono/mono/services/svc-a/app/main.py",
            Assert.Single(report.Files).Path);
        Assert.Equal("/home/runner/work/mono/mono/services/svc-a", Assert.Single(report.SourceRoots));
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public void Parse_TrailingSlashRoot_JoinsWithoutDoubleSlash()
    {
        // DeterministicSourcePaths emits <source>/_/</source> with repo-relative filenames.
        var report = ParseXml(Doc(["/_/"], "src/MyApp/Calculator.cs"));

        Assert.Equal("/_/src/MyApp/Calculator.cs", Assert.Single(report.Files).Path);
    }

    [Fact]
    public void Parse_RelativeRoot_IsPrependedToo()
    {
        // cover2cover emits relative roots like src/main/java with package-relative filenames;
        // the joined key is what distinguishes two modules' com/example/Foo.java from each other.
        var report = ParseXml(Doc(["src/main/java"], "com/example/Foo.java"));

        Assert.Equal("src/main/java/com/example/Foo.java", Assert.Single(report.Files).Path);
    }

    [Theory]
    [InlineData("/abs/path/F.cs", "/abs/path/F.cs")]
    [InlineData("C:/proj/src/F.cs", "C:/proj/src/F.cs")]
    public void Parse_AlreadyRootedFilename_IsNotPrefixed(string filename, string expected)
    {
        // The rooted check is manual (leading '/' or drive-letter prefix): Path.IsPathRooted
        // says "C:/x" is NOT rooted on Linux, and reports cross machines — a Windows-emitted
        // report analyzed in a Linux CI job must not get a root prepended onto C:/.
        var report = ParseXml(Doc(["/some/root"], filename));

        Assert.Equal(expected, Assert.Single(report.Files).Path);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("./")]
    public void Parse_DotSourceRoot_IsANoOp(string root)
    {
        // grcov emits <source>.</source>; prepending "." would change every key while adding
        // no identity information.
        var report = ParseXml(Doc([root], "src/main.rs"));

        Assert.Equal("src/main.rs", Assert.Single(report.Files).Path);
        Assert.Empty(report.SourceRoots);
    }

    [Fact]
    public void Parse_BackslashSourceRoot_IsSeparatorNormalizedBeforeJoining()
    {
        var report = ParseXml(Doc([@"C:\proj"], @"src\A.cs"));

        Assert.Equal("C:/proj/src/A.cs", Assert.Single(report.Files).Path);
    }

    [Fact]
    public void Parse_NoOpRootAlongsideRealRoot_FirstDeclaredStillWins_AndWarns()
    {
        // coverage.py shape: <source>.</source> (the project dir) alongside a site-packages
        // root. The no-op is still the FIRST declared root, so relative filenames stay
        // unprefixed — resolving them against the later real root would rewrite every key
        // against the documented first-wins contract — and the guaranteed multi-root
        // ambiguity warning fires: two declared conventions, files not attributable to a
        // unique root. The no-op keeps its slot in SourceRoots (as "") so a merge can tell
        // this report apart from one that declared only the real root.
        var report = ParseXml(Doc([".", "/usr/lib/python3/dist-packages"], "app/main.py"));

        Assert.Equal("app/main.py", Assert.Single(report.Files).Path);
        Assert.Equal(["", "/usr/lib/python3/dist-packages"], report.SourceRoots);
        var w = Assert.Single(report.Warnings);
        Assert.Equal(CoverageWarningKind.FileIdentityAmbiguous, w.Kind);
        Assert.Contains("unprefixed", w.Detail);
    }

    [Fact]
    public void Parse_RealRootThenNoOpRoot_ResolvesAgainstTheRealFirst_AndWarns()
    {
        // Declaration order decides: with the real root first, relative filenames prefix
        // against it; the trailing no-op still counts as a second convention and warns.
        var report = ParseXml(Doc(["/repo", "."], "app/main.py"));

        Assert.Equal("/repo/app/main.py", Assert.Single(report.Files).Path);
        Assert.Equal(["/repo", ""], report.SourceRoots);
        var w = Assert.Single(report.Warnings);
        Assert.Equal(CoverageWarningKind.FileIdentityAmbiguous, w.Kind);
        Assert.Contains("'/repo'", w.Detail);
    }

    [Fact]
    public void Parse_RepeatedNoOpRoots_AreOneEffectiveRoot_NoWarning()
    {
        // "." and "./" spell the same no-op; both resolve identically, so there is no
        // identity ambiguity to warn about — and the report still declares no roots,
        // byte-identical to the lone-"." behavior.
        var report = ParseXml(Doc([".", "./"], "src/main.rs"));

        Assert.Equal("src/main.rs", Assert.Single(report.Files).Path);
        Assert.Empty(report.SourceRoots);
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public void Parse_DuplicateIdenticalRoots_DeduplicateWithoutWarning()
    {
        // ReportGenerator's merged output repeats the same <source> once per input report:
        // one distinct root, no multi-root ambiguity — and the deduplicated list keeps the
        // merge fast path against a single-root sibling.
        var report = ParseXml(Doc(["/repo", "/repo"], "src/A.cs"));

        Assert.Equal("/repo/src/A.cs", Assert.Single(report.Files).Path);
        Assert.Equal(["/repo"], report.SourceRoots);
        Assert.Empty(report.Warnings);

        var sibling = ParseXml(Doc(["/repo"], "src/B.cs"));
        Assert.Empty(CoverageReport.Merge(report, sibling).Warnings);
    }

    [Fact]
    public void Parse_MultipleSourceRoots_FirstWinsDeterministically_AndWarns()
    {
        // The analyzing machine cannot probe the disk the report came from, so with several
        // roots the first is chosen deterministically and the ambiguity is surfaced.
        var report = ParseXml(Doc(["/first", "/second"], "app/f.cs"));

        Assert.Equal("/first/app/f.cs", Assert.Single(report.Files).Path);
        Assert.Equal(["/first", "/second"], report.SourceRoots);
        var w = Assert.Single(report.Warnings);
        Assert.Equal(CoverageWarningKind.FileIdentityAmbiguous, w.Kind);
        Assert.Contains("/first", w.Detail);
    }

    [Fact]
    public async Task ParseAsync_ResolvesSourceRoots_LikeSync()
    {
        using var stream = new MemoryStream(
            Encoding.UTF8.GetBytes(Doc(["/repo/svc-a"], "app/main.py")));
        var report = await CoberturaParser.ParseAsync(stream);

        Assert.Equal("/repo/svc-a/app/main.py", Assert.Single(report.Files).Path);
        Assert.Equal(["/repo/svc-a"], report.SourceRoots);
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

    [Fact]
    public void Merge_SameRelativeNameUnderDifferentRoots_StaysTwoDistinctFiles()
    {
        // Two DIFFERENT coverage.py files, both named app/main.py, under svc-a and svc-b.
        // Pre-fix these fused into one entry via Math.Max: 8/10 = 80% reported, svc-b's four
        // uncovered lines vanished, and a --min-line 70 gate passed a repo that is at 62.5%.
        var a = ParseXml(SvcDoc("svc-a", HitLines(hit: 8, missed: 2)));
        var b = ParseXml(SvcDoc("svc-b", HitLines(hit: 2, missed: 4)));

        var merged = CoverageReport.Merge(a, b);

        Assert.Equal(2, merged.Files.Count);
        Assert.Equal(16, merged.TotalLines);
        Assert.Equal(10, merged.TotalLinesHit);
        Assert.Equal(0.625, merged.LineRate);
        Assert.Equal(GateOutcome.Fail, merged.Evaluate(70).Outcome);

        // Roots union onto the merged report so a later fold still compares conventions.
        Assert.Equal(2, merged.SourceRoots.Count);
    }

    // ── The two-convention double-count (#14): same file, different path conventions ──

    private static string CoverletDefaultDoc() =>
        Doc(["/"], "home/runner/work/app/app/src/MyApp/Calculator.cs",
            """<line number="10" hits="1" branch="false" /><line number="11" hits="1" branch="false" /><line number="12" hits="0" branch="false" />""");

    private static string DeterministicDoc() =>
        Doc(["/_/"], "src/MyApp/Calculator.cs",
            """<line number="10" hits="1" branch="false" /><line number="11" hits="1" branch="false" /><line number="12" hits="0" branch="false" />""");

    [Fact]
    public void Merge_SameFileUnderTwoPathConventions_KeepsBothEntries_ButWarnsAmbiguousIdentity()
    {
        // Coverlet default (<source>/</source> + machine-absolute filename) vs
        // DeterministicSourcePaths (<source>/_/</source> + repo-relative filename) for the
        // SAME source file. No root arithmetic can unify '/home/runner/.../Calculator.cs'
        // with '/_/src/MyApp/Calculator.cs' without probing a disk this machine may not
        // have, so the double-count stays — but it must be OBSERVABLE, not silent.
        var merged = CoverageReport.Merge(ParseXml(CoverletDefaultDoc()), ParseXml(DeterministicDoc()));

        Assert.Equal(2, merged.Files.Count);   // honest: still double-counted
        var w = Assert.Single(merged.Warnings);
        Assert.Equal(CoverageWarningKind.FileIdentityAmbiguous, w.Kind);
        Assert.Contains("/home/runner/work/app/app/src/MyApp/Calculator.cs", w.Detail);
        Assert.Contains("/_/src/MyApp/Calculator.cs", w.Detail);
    }

    [Fact]
    public void Merge_SameRootsOnBothSides_NeverScansForAmbiguity()
    {
        // Partitioned test runs from the same pipeline (same roots) routinely cover disjoint
        // same-named files — that is not ambiguity, and must produce zero warning noise.
        var a = ParseXml(Doc(["/"], "home/ci/src/A/Util.cs"));
        var b = ParseXml(Doc(["/"], "home/ci/src/B/Util.cs"));

        var merged = CoverageReport.Merge(a, b);

        Assert.Equal(2, merged.Files.Count);
        Assert.Empty(merged.Warnings);
    }

    [Fact]
    public void Merge_HandBuiltReportsWithoutRoots_NeverScansForAmbiguity()
    {
        var a = new CoverageReport([new FileCoverage("x/Program.cs", 1, 2, 0, 0)]);
        var b = new CoverageReport([new FileCoverage("y/Program.cs", 2, 2, 0, 0)]);

        Assert.Empty(CoverageReport.Merge(a, b).Warnings);
    }

    [Fact]
    public void Merge_RootSpellingVariants_TakeTheSameRootsFastPath()
    {
        // Drive-letter case and a trailing slash are spellings, not identities: partitioned
        // Windows CI jobs (c:\agent\work\repo vs C:/agent/work/repo/) key their files under
        // one normalized root, so the merge must treat them as the same-pipeline case —
        // zero cross-convention warnings for genuinely distinct same-named files.
        var a = ParseXml(Doc([@"c:\agent\work\repo"], "src/A/Util.cs"));
        var b = ParseXml(Doc(["C:/agent/work/repo/"], "src/B/Util.cs"));

        var merged = CoverageReport.Merge(a, b);

        Assert.Equal(2, merged.Files.Count);
        Assert.Empty(merged.Warnings);
        Assert.Equal(["C:/agent/work/repo"], merged.SourceRoots);
    }

    [Fact]
    public void Merge_HandBuiltRootSpellingVariants_CompareByNormalizedIdentity()
    {
        // Programmatically constructed reports never pass through the parser's root
        // normalization; the merge-side comparison must normalize for itself rather than
        // read raw spelling as a convention change.
        var a = new CoverageReport([new FileCoverage("x/Util.cs", 1, 2, 0, 0)]) { SourceRoots = [@"c:\repo"] };
        var b = new CoverageReport([new FileCoverage("y/Util.cs", 2, 2, 0, 0)]) { SourceRoots = ["C:/repo/"] };

        Assert.Empty(CoverageReport.Merge(a, b).Warnings);
    }

    // ── Diff: unique-file-name pairing fallback (C1) and the migration effect ──

    [Fact]
    public void Diff_SameFileUnderTwoPathConventions_ReadsUnchanged_NotRemovedPlusAdded()
    {
        // before = Coverlet default convention, after = DeterministicSourcePaths. Identical
        // coverage of the same file must diff as Unchanged via the unique-file-name pairing,
        // not as a -66.67 removal plus a +66.67 addition.
        var result = CoverageDiff.Compare(ParseXml(CoverletDefaultDoc()), ParseXml(DeterministicDoc()));

        var d = Assert.Single(result.Files);
        Assert.Equal(FileChangeKind.Unchanged, d.Change);
        Assert.Equal("/_/src/MyApp/Calculator.cs", d.Path);   // reported under the After identity
        Assert.Empty(result.Added);
        Assert.Empty(result.Removed);
    }

    [Fact]
    public void Diff_PreSourceRootSnapshotAgainstResolvedReport_PairsAsTheSameFile()
    {
        // The migration effect of source-root resolution: FileCoverage.Path values change
        // (e.g. 'src/MyApp/Calculator.cs' → '/_/src/MyApp/Calculator.cs'), so a snapshot
        // taken before this fix carries the old keys. Diffing it against a post-fix report
        // must pair the identities through the file-name fallback instead of reporting the
        // whole codebase as removed+added.
        var preFixSnapshot = new CoverageReport([new FileCoverage("src/MyApp/Calculator.cs", 1, 3, 0, 0)]);
        var postFix = ParseXml(DeterministicDoc());   // 2/3 covered

        var result = CoverageDiff.Compare(preFixSnapshot, postFix);

        var d = Assert.Single(result.Files);
        Assert.Equal(FileChangeKind.Modified, d.Change);
        Assert.NotNull(d.Before);
        Assert.NotNull(d.After);
        Assert.Equal(1.0 / 3, d.Before!.Value, precision: 10);
        Assert.Equal(2.0 / 3, d.After!.Value, precision: 10);
        Assert.Empty(result.Added);
        Assert.Empty(result.Removed);
    }

    [Fact]
    public void Diff_AmbiguousFileNameTail_StaysRemovedPlusAdded()
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

        Assert.Equal(2, result.Removed.Count());
        Assert.Single(result.Added);
    }

    [Fact]
    public void Diff_FileNameTailMatchesOnWholeSegmentsOnly()
    {
        // 'MyCalculator.cs' ends with the raw string "Calculator.cs" but is a different file
        // name — the fallback compares whole final path segments, so no pairing happens.
        var before = new CoverageReport([new FileCoverage("src/MyCalculator.cs", 1, 2, 0, 0)]);
        var after = new CoverageReport([new FileCoverage("src/lib/Calculator.cs", 2, 2, 0, 0)]);

        var result = CoverageDiff.Compare(before, after);

        Assert.Single(result.Removed);
        Assert.Single(result.Added);
    }

    // ── Ordinal keying with normalized keys (C2) ──

    [Fact]
    public void Merge_CaseDistinctFilenames_StayDistinctAcrossReports()
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

        Assert.Equal(2, merged.Files.Count);
        Assert.Equal(0.5, merged.LineRate);
    }

    [Fact]
    public void Parse_LowercaseDriveLetter_NormalizesToUppercaseKey()
    {
        // Windows toolchains disagree on drive-letter casing; Ordinal keying gets its
        // Windows cross-report stability from normalizing the KEY, not from a
        // case-insensitive comparer (a Dictionary has one comparer for every key).
        var report = ParseXml(Doc([], "c:/proj/src/A.cs"));

        Assert.Equal("C:/proj/src/A.cs", Assert.Single(report.Files).Path);
    }

    [Fact]
    public void Merge_DriveLetterCaseAndSeparatorVariants_UnionAsOneFile()
    {
        var a = ParseXml(Doc([], @"c:\proj\src\A.cs"));
        var b = ParseXml(Doc([], "C:/proj/src/A.cs"));

        var merged = CoverageReport.Merge(a, b);

        Assert.Equal("C:/proj/src/A.cs", Assert.Single(merged.Files).Path);
    }

    [Fact]
    public void Exclude_PreservesSourceRoots()
    {
        var report = ParseXml(Doc(["/repo"], "src/A.cs"));

        var filtered = report.Exclude(["nothing-matches"]);

        Assert.Equal(["/repo"], filtered.SourceRoots);
    }
}
