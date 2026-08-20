using System.Text;
using DotCov.Tests.Infrastructure;
using Xunit;

namespace DotCov.Tests;

/// <summary>
/// Run-3 mutation-gate kills: each test here kills at least one Stryker mutant that survived
/// the full 483-test suite at commit 95017fd. Targets are the real behavioral gaps in the
/// verdict/merge/parser areas — parser source-root handling, file-identity normalization,
/// merge divergence warnings, and the gate's epsilon boundary — not formatter cosmetics.
/// </summary>
public sealed class MutationKills3
{
    private static CoverageReport ParseXml(string xml) =>
        CoberturaParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

    /// <summary>One-class document with optional <c>&lt;source&gt;</c> roots (raw, unescaped).</summary>
    private static string Doc(
        string sources,
        string filename,
        string lines = """<line number="1" hits="1" branch="false" />""") =>
        $"""
         <?xml version="1.0"?>
         <coverage>{sources}<packages><package><classes>
           <class name="X" filename="{filename}"><lines>{lines}</lines></class>
         </classes></package></packages></coverage>
         """;

    // ── ConsumeSource: the reader-positioning contract ──

    [Fact]
    public void Parse_EmptySourceElement_DoesNotSwallowTheFollowingRoot()
    {
        // Kills CoberturaParser.cs:219 (remove `if (reader.IsEmptyElement) return;`).
        // Without the guard, ConsumeSource issues an extra Read() on `<source/>` that leaves
        // the cursor ON the next <source> start tag; the main loop's own Read() then jumps
        // INTO that element's text, so the second (real) root is silently lost and every
        // relative filename loses its identity prefix.
        var report = ParseXml(Doc("<sources><source/><source>/repo</source></sources>", "app/main.py"));

        Assert.Equal("/repo", Assert.Single(report.SourceRoots));
        Assert.Equal("/repo/app/main.py", Assert.Single(report.Files).Path);
    }

    [Fact]
    public void Parse_CommentInsideSource_IsNotARoot()
    {
        // Kills CoberturaParser.cs:220 (`||` -> `&&` in the Read/NodeType guard). The mutant
        // short-circuits past the node-type check whenever Read() succeeds, so a Comment
        // node's Value ("ci checkout") becomes a bogus source root and rewrites every file key.
        var report = ParseXml(Doc(
            "<sources><source><!--ci checkout--></source></sources>", "app/main.py"));

        Assert.Empty(report.SourceRoots);
        Assert.Equal("app/main.py", Assert.Single(report.Files).Path);
    }

    [Fact]
    public void Parse_MultipleRoots_WarningIsReportScopedWithEmptyFile()
    {
        // Kills CoberturaParser.cs:232 (File: "" -> "Stryker was here!"). The multi-root
        // FileIdentityAmbiguous warning is report-scoped: consumers keying warnings by file
        // must see the documented empty-string File and zero Line, and the detail must name
        // the root actually used for resolution.
        var report = ParseXml(Doc("<sources><source>/a</source><source>/b</source></sources>", "x.cs"));

        var w = Assert.Single(report.Warnings);
        Assert.Equal(CoverageWarningKind.FileIdentityAmbiguous, w.Kind);
        Assert.Equal("", w.File);
        Assert.Equal(0, w.Line);
        Assert.Contains("'/a'", w.Detail, StringComparison.Ordinal);
    }

    // ── ConsumeClass: the -1 condition-line sentinel ──

    [Fact]
    public void Parse_BranchOnLineZero_StillCollectsConditionDetail()
    {
        // Kills CoberturaParser.cs:276 (`conditionLine >= 0` -> `> 0`). The no-current-line
        // sentinel is -1 precisely so that line number 0 stays a valid attribution target;
        // the mutant silently drops per-condition detail for line 0, degrading its cross-report
        // merge to the line-level aggregate.
        var f = Cobertura.NewDoc()
            .AddClass("z.cs", c => c.BranchWithConditions(0, "50% (1/2)", (0, "50%")))
            .Parse().Files[0];

        Assert.Equal((1, 2), f.BranchesByLine[0]);
        var conds = Assert.Single(f.ConditionsByLine).Value;
        Assert.Equal(1, conds[0]);
    }

    // ── ResolveFileKey: drive-letter normalization must touch ONLY drive-letter paths ──

    [Fact]
    public void Parse_LowercaseRelativePath_IsNotDriveLetterUppercased()
    {
        // Kills the CoberturaParser.cs:368 `&&`->`||` mutants: with either of the first two
        // conjuncts weakened to a disjunction, ANY lowercase-starting path ("src/app.cs")
        // gets its first character uppercased, forking the file's identity key away from
        // every other report that spells it correctly.
        var f = Cobertura.NewDoc()
            .AddClass("src/app.cs", c => c.Line(1, hits: 1))
            .Parse().Files[0];

        Assert.Equal("src/app.cs", f.Path);
    }

    [Fact]
    public void Parse_TwoCharDirectoryPath_IsNotDriveLetterUppercased()
    {
        // Kills CoberturaParser.cs:368 third-`&&`->`||`: `(len && lower && colon) || f[2]=='/'`
        // fires for any two-character directory ("ab/foo.py" has '/' at index 2) and
        // uppercases a path that has nothing to do with Windows drives.
        var f = Cobertura.NewDoc()
            .AddClass("ab/foo.py", c => c.Line(1, hits: 1))
            .Parse().Files[0];

        Assert.Equal("ab/foo.py", f.Path);
    }

    [Fact]
    public void Parse_BareDriveRoot_ExactlyThreeChars_IsStillNormalized()
    {
        // Kills CoberturaParser.cs:368 `>= 3` -> `> 3`: the shortest possible drive-rooted
        // path ("c:/", length exactly 3) sits on the boundary and must still normalize its
        // drive letter so `c:\` and `C:/` spellings produce one Ordinal key.
        var f = Cobertura.NewDoc()
            .AddClass("c:\\", c => c.Line(1, hits: 1))
            .Parse().Files[0];

        Assert.Equal("C:/", f.Path);
    }

    [Fact]
    public void Parse_EmptyFilenameWithSourceRoot_DoesNotThrow()
    {
        // Kills CoberturaParser.cs:376 `&&`->`||` in IsRooted: the mutant evaluates
        // `char.IsAsciiLetter(path[0])` on a zero-length filename and throws
        // IndexOutOfRangeException. An empty filename attribute is degenerate emitter
        // output, but the parser's contract is warnings-not-crashes for malformed input.
        var report = ParseXml(Doc("<sources><source>/repo</source></sources>", ""));

        Assert.Equal("/repo/", Assert.Single(report.Files).Path);
    }

    [Fact]
    public void Parse_TwoCharDriveRelativeFilename_CountsAsRooted()
    {
        // Kills CoberturaParser.cs:376 `>= 2` -> `> 2`: "c:" (length exactly 2) is a
        // drive-relative Windows path — already rooted, so the <source> root must NOT be
        // prepended. The mutant reclassifies it as relative and invents "/repo/c:".
        var report = ParseXml(Doc("<sources><source>/repo</source></sources>", "c:"));

        Assert.Equal("c:", Assert.Single(report.Files).Path);
    }

    // ── MergeWith: the ConditionIdentityMismatch warning payload ──

    [Fact]
    public void Merge_ConditionIdentityMismatch_DetailNamesBothNumberSetsAscending()
    {
        // Kills the CoverageReport.cs:403-404 string mutants and both Order() ->
        // OrderDescending() mutants: the warning detail is the only place the divergent
        // condition-number sets are surfaced, and its documented rendering is each side's
        // numbers in ascending order.
        var a = Cobertura.NewDoc()
            .AddClass("x.cs", c => c.BranchWithConditions(5, "50% (2/4)", (1, "50%"), (3, "50%")))
            .Parse();
        var b = Cobertura.NewDoc()
            .AddClass("x.cs", c => c.BranchWithConditions(5, "50% (2/4)", (2, "100%"), (4, "0%")))
            .Parse();

        var merged = CoverageReport.Merge(a, b);

        var w = Assert.Single(merged.Warnings);
        Assert.Equal(CoverageWarningKind.ConditionIdentityMismatch, w.Kind);
        Assert.Equal("x.cs", w.File);
        Assert.Equal(5, w.Line);
        Assert.Equal("condition numbers [1,3] vs [2,4] - using the line aggregate", w.Detail);
    }

    // ── Merge: cross-convention file-identity ambiguity ──

    [Fact]
    public void Merge_RootedWithRootless_StillWarnsOnAmbiguousFileIdentity()
    {
        // Kills the CoverageReport.cs:700 guard mutants (`&&` -> `||`, `is 0` -> `is not 0`):
        // the flagship ambiguity scenario is exactly one side declaring a <source> root while
        // the other declares none (Coverlet default emits an EMPTY <source></source>, so its
        // root list is empty). The mutants skip the scan whenever either side is root-less,
        // silencing the warning in the very case it exists for.
        //
        // Also kills CoverageReport.cs:723 (`LastIndexOf('/') - 1`): the root-less side's
        // bare "app.cs" makes the mutated FileNameOf slice from index -2 and throw.
        // The extra a-only file with a non-matching name ("only.cs") exercises the
        // no-candidate `continue` (previously NoCoverage — a removed continue NREs).
        var rooted = ParseXml(
            """
            <?xml version="1.0"?>
            <coverage><sources><source>/repo</source></sources><packages><package><classes>
              <class name="A" filename="src/app.cs"><lines><line number="1" hits="1" branch="false" /></lines></class>
              <class name="B" filename="src/only.cs"><lines><line number="1" hits="1" branch="false" /></lines></class>
            </classes></package></packages></coverage>
            """);
        var rootless = ParseXml(Doc("", "app.cs"));

        var w = Assert.Single(CoverageReport.Merge(rooted, rootless).Warnings);
        Assert.Equal(CoverageWarningKind.FileIdentityAmbiguous, w.Kind);
        Assert.Contains("/repo/src/app.cs", w.Detail, StringComparison.Ordinal);
        Assert.Contains("'app.cs'", w.Detail, StringComparison.Ordinal);

        // Mirrored order kills the symmetric `b.SourceRoots.Count is not 0` guard mutant:
        // the ambiguity scan must fire regardless of which side carries the root.
        var mirrored = Assert.Single(CoverageReport.Merge(rootless, rooted).Warnings);
        Assert.Equal(CoverageWarningKind.FileIdentityAmbiguous, mirrored.Kind);
    }

    // ── CoverageDiff: the movement-epsilon boundary ──

    [Fact]
    public void Compare_DeltaExactlyMovementEpsilon_IsModified()
    {
        // Kills CoverageDiff.cs:222 (`<` -> `<=`). MovementEpsilon's contract is "a rate delta
        // CLOSER TO ZERO than this is measurement noise" — a movement of exactly epsilon is
        // movement. 1.0/10000 is the correctly-rounded IEEE 754 quotient of the same real
        // number the 0.0001 literal rounds to, so `after.LineRate - before.LineRate` equals
        // MovementEpsilon EXACTLY and only the strict `<` classifies the file as Modified.
        // The mutant would also desync Compare's change kind from AnsiPen.Delta's coloring,
        // which shares the constant precisely so they can never disagree.
        var before = Reports.Single("a.cs", hit: 0, total: 10000);
        var after = Reports.Single("a.cs", hit: 1, total: 10000);

        var result = CoverageDiff.Compare(before, after);

        var file = Assert.Single(result.Files);
        Assert.Equal(CoverageDiff.MovementEpsilon, file.Delta);
        Assert.Equal(FileChangeKind.Modified, file.Change);
    }

    // ── GateResult: the verdict comparison itself ──

    [Fact]
    public void Evaluate_RateExactlyOnEpsilonBoundary_Passes()
    {
        // Kills GateResult.cs:74 (`>=` -> `>`). With rate 25/100, `rate * 100` is exactly
        // 25.0, and `(25.0 + 1e-9) - RateEpsilon` computes back to exactly 25.0 in IEEE 754
        // — so the two sides of MeetsThreshold are EQUAL and only `>=` honors the documented
        // "an exactly-met threshold passes" contract.
        var gate = Reports.Single("a.cs", hit: 25, total: 100).Evaluate(25.0 + 1e-9);

        Assert.Equal(GateOutcome.Pass, gate.Outcome);
        Assert.True(gate.IsPass);
    }

    [Fact]
    public void BranchBelowThreshold_UnarmedGate_NeverReportsBelow()
    {
        // Kills GateResult.cs:84 (`MinBranchPercent > 0` -> `>= 0`). BranchBelowThreshold's
        // contract requires an ARMED branch threshold; with MinBranchPercent = 0 it must be
        // false no matter what the rate field carries — even out-of-range junk in a
        // hand-built GateResult must not flip an unarmed gate to "below threshold".
        var gate = new GateResult(GateOutcome.Pass, 1.0, -0.5, 80, 0, "unarmed");

        Assert.False(gate.BranchBelowThreshold);
    }
}
