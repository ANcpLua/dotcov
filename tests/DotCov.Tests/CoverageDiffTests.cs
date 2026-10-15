using DotCov.Formatters;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;
[TestClass]
public sealed class CoverageDiffTests
{
    private static CoverageReport Make(params FileCoverage[] files) => new(files);
    [TestMethod]
    public void Compare_IdenticalReports_AllDeltasZero()
    {
        var report = Make(new FileCoverage("a.cs", 8, 10, 0, 0));
        var result = CoverageDiff.Compare(report, report);
        Assert.ContainsSingle(result.Files);
        Assert.AreEqual(0.0, result.Files[0].Delta);
        Assert.AreEqual(FileChangeKind.Unchanged, result.Files[0].Change);
    }

    [TestMethod]
    public void Compare_ImprovedCoverage_PositiveDelta()
    {
        var before = Make(new FileCoverage("a.cs", 5, 10, 0, 0));
        var after = Make(new FileCoverage("a.cs", 8, 10, 0, 0));
        var result = CoverageDiff.Compare(before, after);
        Assert.AreEqual(0.3, result.Files[0].Delta!.Value, 1e-10);
        Assert.AreEqual(FileChangeKind.Modified, result.Files[0].Change);
    }

    [TestMethod]
    public void Compare_RegressionInCoverage_NegativeDelta()
    {
        var before = Make(new FileCoverage("a.cs", 9, 10, 0, 0));
        var after = Make(new FileCoverage("a.cs", 6, 10, 0, 0));
        var result = CoverageDiff.Compare(before, after);
        Assert.IsTrue(result.Files[0].Delta < 0);
        Assert.ContainsSingle(result.Regressions);
    }

    [TestMethod]
    public void Compare_NewFileInAfter_MarkedAsAdded()
    {
        var before = Make();
        var after = Make(new FileCoverage("new.cs", 5, 10, 0, 0));
        var result = CoverageDiff.Compare(before, after);
        Assert.ContainsSingle(result.Files);
        Assert.IsNull(result.Files[0].Before);
        Assert.AreEqual(FileChangeKind.Added, result.Files[0].Change);
        Assert.ContainsSingle(result.Added);
    }

    [TestMethod]
    public void Compare_RemovedFile_MarkedAsRemoved()
    {
        var before = Make(new FileCoverage("old.cs", 8, 10, 0, 0));
        var after = Make();
        var result = CoverageDiff.Compare(before, after);
        Assert.ContainsSingle(result.Files);
        Assert.IsNull(result.Files[0].After);
        Assert.AreEqual(FileChangeKind.Removed, result.Files[0].Change);
        Assert.ContainsSingle(result.Removed);
    }

    [TestMethod]
    public void Compare_Summary_ReportsOverallDelta()
    {
        var before = Make(new FileCoverage("a.cs", 5, 10, 0, 0));
        var after = Make(new FileCoverage("a.cs", 8, 10, 0, 0));
        var result = CoverageDiff.Compare(before, after);
        Assert.AreEqual(0.5, result.BeforeRate);
        Assert.AreEqual(0.8, result.AfterRate);
        Assert.AreEqual(0.3, result.Delta!.Value, 1e-10);
    }

    [TestMethod]
    public void Compare_OrdersByDeltaAscending_WorstFirst()
    {
        var before = Make(new FileCoverage("good.cs", 5, 10, 0, 0), new FileCoverage("bad.cs", 9, 10, 0, 0));
        var after = Make(new FileCoverage("good.cs", 9, 10, 0, 0), new FileCoverage("bad.cs", 3, 10, 0, 0));
        var result = CoverageDiff.Compare(before, after);
        Assert.AreEqual("bad.cs", result.Files[0].Path);
        Assert.AreEqual("good.cs", result.Files[1].Path);
    }

    [TestMethod]
    public void Compare_DirectoryCaseDrift_PairsViaUniqueFileNameFallback()
    {
        // Exact path matching is Ordinal (case-differing paths are distinct files on the
        // case-sensitive filesystems Cobertura's native emitters run on). A directory-casing
        // drift between two uploads of the same file still pairs — through the unique
        // file-name fallback, since 'App.cs' is carried by exactly one leftover file on each
        // side — instead of reading as removed+added.
        var before = Make(new FileCoverage("SRC/App.cs", 5, 10, 0, 0));
        var after = Make(new FileCoverage("src/App.cs", 8, 10, 0, 0));
        var result = CoverageDiff.Compare(before, after);
        var d = Assert.ContainsSingle(result.Files);
        Assert.IsNotNull(d.Before);
        Assert.AreEqual(FileChangeKind.Modified, d.Change);
        Assert.AreEqual("src/App.cs", d.Path);
    }

    [TestMethod]
    public void Compare_SingleSameNamedPairUnderEqualRoots_StaysRemovedPlusAdded()
    {
        // svc-a/Program.cs deleted while svc-b/Program.cs appears: the names collide but the
        // reports carry no evidence of a path-convention change (roots equal — here both
        // empty — no multi-segment suffix agreement, not a casing drift). Pairing them would
        // fabricate a Modified entry with line changes neither report contains and suppress
        // the honest Removed record; the fallback must leave them apart.
        var before = Make(new FileCoverage("svc-a/Program.cs", 8, 10, 0, 0) { LineHits = new Dictionary<int, int> { [1] = 1 } });
        var after = Make(new FileCoverage("svc-b/Program.cs", 1, 10, 0, 0) { LineHits = new Dictionary<int, int> { [1] = 0 } });
        var result = CoverageDiff.Compare(before, after);
        Assert.AreEqual(2, result.Files.Count);
        Assert.AreEqual("svc-a/Program.cs", result.Removed.Single().Path);
        Assert.AreEqual("svc-b/Program.cs", result.Added.Single().Path);
        Assert.DoesNotContain(f => f.Change is FileChangeKind.Modified, result.Files);
        foreach (var f in result.Files)
            Assert.IsEmpty(f.LineChanges);
    }

    [TestMethod]
    public void Compare_MultiSegmentSuffixAgreement_PairsEvenWithoutDeclaredRoots()
    {
        // Hand-built snapshots carry no source roots, so a prefix migration must pair through
        // path evidence alone: two whole trailing segments agree (MyApp/Calculator.cs) —
        // which a bare name collision (svc-a/Program.cs vs svc-b/Program.cs) never satisfies.
        var before = Make(new FileCoverage("src/MyApp/Calculator.cs", 1, 3, 0, 0));
        var after = Make(new FileCoverage("/_/src/MyApp/Calculator.cs", 2, 3, 0, 0));
        var result = CoverageDiff.Compare(before, after);
        var d = Assert.ContainsSingle(result.Files);
        Assert.AreEqual(FileChangeKind.Modified, d.Change);
        Assert.AreEqual("/_/src/MyApp/Calculator.cs", d.Path);
        Assert.IsEmpty(result.Added);
        Assert.IsEmpty(result.Removed);
    }

    [TestMethod]
    public void Compare_CaseDistinctFileNames_StayDistinctAndMatchExactly()
    {
        // xt_TCPMSS.c and xt_tcpmss.c genuinely coexist (linux/net/netfilter). Ordinal keying
        // matches each exactly; the file-name fallback never crosses them because final-segment
        // comparison is Ordinal too.
        var before = Make(new FileCoverage("net/xt_TCPMSS.c", 4, 4, 0, 0), new FileCoverage("net/xt_tcpmss.c", 0, 4, 0, 0));
        var after = Make(new FileCoverage("net/xt_TCPMSS.c", 4, 4, 0, 0), new FileCoverage("net/xt_tcpmss.c", 2, 4, 0, 0));
        var result = CoverageDiff.Compare(before, after);
        Assert.AreEqual(2, result.Files.Count);
        var upper = result.Files.Single(f => f.Path == "net/xt_TCPMSS.c");
        var lower = result.Files.Single(f => f.Path == "net/xt_tcpmss.c");
        Assert.AreEqual(FileChangeKind.Unchanged, upper.Change);
        Assert.AreEqual(FileChangeKind.Modified, lower.Change);
        Assert.AreEqual(0.5, lower.Delta);
    }

    [TestMethod]
    public void Compare_RemovedZeroRateFile_IsARegression_AddedZeroRateFile_IsNotAnImprovement()
    {
        // Losing a MEASURED file is a regression of what the report vouches for,
        // whatever its rate — a removed 0% file included. Its numeric delta is positive zero
        // (never -0.0, which would render as "+-0.0%"). A new 0% file adds nothing that could
        // count as an improvement, and is not a regression either.
        var before = Make(new FileCoverage("gone.cs", 0, 2, 0, 0));
        var after = Make(new FileCoverage("fresh.cs", 0, 2, 0, 0));
        var result = CoverageDiff.Compare(before, after);
        var removed = result.Removed.Single();
        Assert.AreEqual("gone.cs", removed.Path);
        Assert.AreEqual(0.0, removed.Delta);
        Assert.IsFalse(double.IsNegative(removed.Delta!.Value));
        Assert.IsTrue(removed.IsRegression);
        Assert.IsFalse(removed.IsImprovement);
        Assert.AreEqual("gone.cs", result.Regressions.Single().Path);
        var added = result.Added.Single();
        Assert.AreEqual("fresh.cs", added.Path);
        Assert.AreEqual(0.0, added.Delta);
        Assert.IsFalse(added.IsRegression);
        Assert.IsFalse(added.IsImprovement);
        Assert.IsEmpty(result.Improvements);
    }

    [TestMethod]
    public void FormatDiff_RemovedZeroRateFile_NeverRendersADoubleSign()
    {
        // Control: a removed file with a positive rate renders its own '-'; the removed 0%
        // file renders "+0.0%" — the sign policy is "+ for >= 0", so the delta itself must be
        // positive zero rather than the text being patched afterwards.
        var before = Make(new FileCoverage("gone.cs", 0, 2, 0, 0), new FileCoverage("ctrl.cs", 8, 10, 0, 0));
        var after = Make();
        var result = CoverageDiff.Compare(before, after);
        var table = AnsiStrip.From(TableFormatter.FormatDiff(result));
        var md = MarkdownFormatter.FormatDiff(result);
        Assert.DoesNotContain("+-", table);
        Assert.DoesNotContain("+-", md);
        Assert.MatchesRegex(@"gone\.cs\s+0\.0%\s+-\s+\+0\.0%\s+Removed", table);
        Assert.MatchesRegex(@"ctrl\.cs\s+80\.0%\s+-\s+-80\.0%\s+Removed", table);
        Assert.Contains("| `gone.cs` | 0.0% | - | +0.0% | Removed |", md);
        Assert.Contains("| `ctrl.cs` | 80.0% | - | -80.0% | Removed |", md);
    }

    public static IEnumerable<(string Scenario, FileCoverage? Before, FileCoverage? After, double? Delta, FileChangeKind Change, bool Regression, bool Improvement)> ClassificationCases()
    {
        // Removed: a measured file is a regression whatever its rate; unmeasured is not.
        yield return ("removed 0%", new("f.cs", 0, 2, 0, 0), null, 0.0, FileChangeKind.Removed, true, false);
        yield return ("removed 80%", new("f.cs", 8, 10, 0, 0), null, -0.8, FileChangeKind.Removed, true, false);
        yield return ("removed unmeasured", new("f.cs", 0, 0, 0, 0), null, null, FileChangeKind.Removed, false, false);
        // Added: only a measured, non-zero rate is an improvement.
        yield return ("added 0%", null, new("f.cs", 0, 2, 0, 0), 0.0, FileChangeKind.Added, false, false);
        yield return ("added 70%", null, new("f.cs", 7, 10, 0, 0), 0.7, FileChangeKind.Added, false, true);
        yield return ("added unmeasured", null, new("f.cs", 0, 0, 0, 0), null, FileChangeKind.Added, false, false);
        // Both sides: movement only with comparable measurements and at least MovementEpsilon.
        yield return ("both 0%", new("f.cs", 0, 2, 0, 0), new("f.cs", 0, 2, 0, 0), 0.0, FileChangeKind.Unchanged, false, false);
        yield return ("both 50%", new("f.cs", 5, 10, 0, 0), new("f.cs", 5, 10, 0, 0), 0.0, FileChangeKind.Unchanged, false, false);
        yield return ("up 50→80", new("f.cs", 5, 10, 0, 0), new("f.cs", 8, 10, 0, 0), 0.3, FileChangeKind.Modified, false, true);
        yield return ("down 80→50", new("f.cs", 8, 10, 0, 0), new("f.cs", 5, 10, 0, 0), -0.3, FileChangeKind.Modified, true, false);
        yield return ("both unmeasured", new("f.cs", 0, 0, 0, 0), new("f.cs", 0, 0, 0, 0), null, FileChangeKind.Unchanged, false, false);
        yield return ("measured→unmeasured", new("f.cs", 5, 10, 0, 0), new("f.cs", 0, 0, 0, 0), null, FileChangeKind.Unchanged, false, false);
        yield return ("unmeasured→measured", new("f.cs", 0, 0, 0, 0), new("f.cs", 5, 10, 0, 0), null, FileChangeKind.Unchanged, false, false);
        yield return ("below epsilon down", new("f.cs", 19999, 20000, 0, 0), new("f.cs", 19998, 20000, 0, 0), -0.00005, FileChangeKind.Unchanged, false, false);
        yield return ("below epsilon up", new("f.cs", 19998, 20000, 0, 0), new("f.cs", 19999, 20000, 0, 0), 0.00005, FileChangeKind.Unchanged, false, false);
        yield return ("exactly epsilon up", new("f.cs", 0, 10000, 0, 0), new("f.cs", 1, 10000, 0, 0), CoverageDiff.MovementEpsilon, FileChangeKind.Modified, false, true);
        yield return ("exactly epsilon down", new("f.cs", 1, 10000, 0, 0), new("f.cs", 0, 10000, 0, 0), -CoverageDiff.MovementEpsilon, FileChangeKind.Modified, true, false);
    }

    [TestMethod]
    [DynamicData(nameof(ClassificationCases))]
    public void FileDelta_ClassificationTable_SingleFlagsAndResultFiltersAgree(string scenario, FileCoverage? before, FileCoverage? after, double? delta, FileChangeKind change, bool regression, bool improvement)
    {
        var result = CoverageDiff.Compare(before is { } b ? Make(b) : Make(), after is { } a ? Make(a) : Make());
        var d = Assert.ContainsSingle(result.Files);
        Assert.AreEqual(change, d.Change, message: scenario);
        if (delta is { } expected)
        {
            Assert.IsNotNull(d.Delta, message: scenario);
            Assert.AreEqual(expected, d.Delta!.Value, 1e-12, message: scenario);
            Assert.IsFalse(double.IsNegative(d.Delta.Value) && d.Delta.Value == 0, message: $"{scenario}: no negative zero");
        }
        else
        {
            Assert.IsNull(d.Delta, message: scenario);
        }

        Assert.AreEqual(regression, d.IsRegression, message: scenario);
        Assert.AreEqual(improvement, d.IsImprovement, message: scenario);
        Assert.AreEqual(regression, result.Regressions.Any(), message: $"{scenario}: Regressions filter");
        Assert.AreEqual(improvement, result.Improvements.Any(), message: $"{scenario}: Improvements filter");
    }

    [TestMethod]
    public void Compare_LineFlippedFromHitToMiss_SurfacesAsNewlyMissed()
    {
        var before = Make(new FileCoverage("a.cs", 1, 1, 0, 0) { LineHits = new Dictionary<int, int> { [10] = 3 } });
        var after = Make(new FileCoverage("a.cs", 0, 1, 0, 0) { LineHits = new Dictionary<int, int> { [10] = 0 } });
        var result = CoverageDiff.Compare(before, after);
        var fileDelta = Assert.ContainsSingle(result.Files);
        var lineDelta = Assert.ContainsSingle(fileDelta.LineChanges);
        var newlyMissed = (Assert.IsExactInstanceOfType<LineDelta.NewlyMissed>(lineDelta));
        Assert.AreEqual(10, newlyMissed.Line);
        Assert.AreEqual(3, newlyMissed.BeforeHits);
        Assert.AreEqual(0, newlyMissed.AfterHits);
    }

    [TestMethod]
    public void Compare_LineFlippedFromMissToHit_SurfacesAsNewlyHit()
    {
        var before = Make(new FileCoverage("a.cs", 0, 1, 0, 0) { LineHits = new Dictionary<int, int> { [10] = 0 } });
        var after = Make(new FileCoverage("a.cs", 1, 1, 0, 0) { LineHits = new Dictionary<int, int> { [10] = 5 } });
        var result = CoverageDiff.Compare(before, after);
        var lineDelta = Assert.ContainsSingle(result.Files[0].LineChanges);
        var newlyHit = (Assert.IsExactInstanceOfType<LineDelta.NewlyHit>(lineDelta));
        Assert.AreEqual(0, newlyHit.BeforeHits);
        Assert.AreEqual(5, newlyHit.AfterHits);
    }

    [TestMethod]
    public void Compare_AddedAndRemovedLines_AppearWithRespectiveKindsAndPayloads()
    {
        var before = Make(new FileCoverage("a.cs", 1, 1, 0, 0) { LineHits = new Dictionary<int, int> { [10] = 1, [20] = 4 } });
        var after = Make(new FileCoverage("a.cs", 1, 1, 0, 0) { LineHits = new Dictionary<int, int> { [10] = 1, [30] = 7 } });
        var result = CoverageDiff.Compare(before, after);
        var changes = result.Files[0].LineChanges;
        Assert.AreEqual(2, changes.Count);
        var removed = (Assert.IsExactInstanceOfType<LineDelta.Removed>(changes.Single(c => c.Line == 20)));
        Assert.AreEqual(4, removed.BeforeHits);
        var added = (Assert.IsExactInstanceOfType<LineDelta.Added>(changes.Single(c => c.Line == 30)));
        Assert.AreEqual(7, added.AfterHits);
    }

    [TestMethod]
    public void Compare_LineChanges_AreSortedAfterFilteringUnchangedLines()
    {
        var before = Make(new FileCoverage("a.cs", 2, 4, 0, 0) { LineHits = new Dictionary<int, int> { [1000] = 1, [20] = 4, [10] = 0, [30] = 1 } });
        var after = Make(new FileCoverage("a.cs", 2, 4, 0, 0) { LineHits = new Dictionary<int, int> { [1000] = 1, [30] = 0, [40] = 7, [10] = 5 } });
        var result = CoverageDiff.Compare(before, after);
        Assert.AreSequenceEqual([10, 20, 30, 40], result.Files[0].LineChanges.Select(c => c.Line), SequenceOrder.InOrder);
    }

    [TestMethod]
    public void Compare_UnchangedHitCount_ProducesNoLineChange()
    {
        var both = new FileCoverage("a.cs", 1, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int>
            {
                [10] = 5
            }
        };
        var result = CoverageDiff.Compare(Make(both), Make(both));
        Assert.IsEmpty(result.Files[0].LineChanges);
    }

    [TestMethod]
    public void Compare_HitCountChangedButStillHit_ProducesNoLineChange()
    {
        var before = Make(new FileCoverage("a.cs", 1, 1, 0, 0) { LineHits = new Dictionary<int, int> { [10] = 100 } });
        var after = Make(new FileCoverage("a.cs", 1, 1, 0, 0) { LineHits = new Dictionary<int, int> { [10] = 1 } });
        var result = CoverageDiff.Compare(before, after);
        Assert.IsEmpty(result.Files[0].LineChanges);
    }

    [TestMethod]
    public void CoverageDiffResult_WithLineChanges_FiltersFilesWithFlippedLines()
    {
        var before = Make(new FileCoverage("flipped.cs", 1, 1, 0, 0) { LineHits = new Dictionary<int, int> { [10] = 1 } }, new FileCoverage("stable.cs", 1, 1, 0, 0) { LineHits = new Dictionary<int, int> { [20] = 1 } });
        var after = Make(new FileCoverage("flipped.cs", 0, 1, 0, 0) { LineHits = new Dictionary<int, int> { [10] = 0 } }, new FileCoverage("stable.cs", 1, 1, 0, 0) { LineHits = new Dictionary<int, int> { [20] = 1 } });
        var result = CoverageDiff.Compare(before, after);
        var affected = Assert.ContainsSingle(result.WithLineChanges);
        Assert.AreEqual("flipped.cs", affected.Path);
        Assert.AreEqual(1, result.TotalLineChanges);
    }

    [TestMethod]
    public void Compare_AddedOrRemovedFile_HasNoLineChanges()
    {
        var before = Make(new FileCoverage("gone.cs", 1, 1, 0, 0) { LineHits = new Dictionary<int, int> { [10] = 1 } });
        var after = Make(new FileCoverage("fresh.cs", 1, 1, 0, 0) { LineHits = new Dictionary<int, int> { [20] = 1 } });
        var result = CoverageDiff.Compare(before, after);
        foreach (var f in result.Files)
            Assert.IsEmpty(f.LineChanges);
    }

    [TestMethod]
    public void Compare_LineMissedOnBothSides_ProducesNoLineChange()
    {
        var both = new FileCoverage("a.cs", 0, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int>
            {
                [10] = 0
            }
        };
        var result = CoverageDiff.Compare(Make(both), Make(both));
        Assert.IsEmpty(result.Files[0].LineChanges);
    }

    [TestMethod]
    public void LineDelta_Match_RoutesEachVariantToItsOwnArm()
    {
        // Match<T> is the value-returning half of the visitor pair; production uses Switch, so
        // nothing else exercises these four copy-paste-shaped overrides — pin that each routes to
        // its own arm (a mis-wire like Added.Match -> removed(this) would otherwise go unnoticed).
        string Tag(LineDelta d) => d.Match(added: _ => "added", removed: _ => "removed", newlyHit: _ => "newlyHit", newlyMissed: _ => "newlyMissed");
        Assert.AreEqual("added", Tag(new LineDelta.Added(1, 5)));
        Assert.AreEqual("removed", Tag(new LineDelta.Removed(2, 3)));
        Assert.AreEqual("newlyHit", Tag(new LineDelta.NewlyHit(3, 0, 4)));
        Assert.AreEqual("newlyMissed", Tag(new LineDelta.NewlyMissed(4, 7, 0)));
    }

    [TestMethod]
    public void Compare_UnmeasuredOnBothSides_IsUnchangedWithNullDelta()
    {
        // A file both reports list but neither measured has no rates to compare: unmeasured
        // on both ends is unchanged, not modified — and the delta is null, not 0.
        var before = Make(new FileCoverage("empty.cs", 0, 0, 0, 0));
        var after = Make(new FileCoverage("empty.cs", 0, 0, 0, 0));
        var result = CoverageDiff.Compare(before, after);
        var d = Assert.ContainsSingle(result.Files);
        Assert.AreEqual(FileChangeKind.Unchanged, d.Change);
        Assert.IsNull(d.Delta);
        Assert.IsEmpty(result.Regressions);
        Assert.IsEmpty(result.Improvements);
    }

    [TestMethod]
    public void Compare_SubEpsilonDelta_IsUnchangedEverywhere_ButLineChangesStillSurface()
    {
        // One line flipping in a 20,000-line file moves the rate by 0.00005 — inside
        // MovementEpsilon. Every movement view must agree it's noise: Change is Unchanged,
        // the file is in NEITHER Regressions nor Improvements (they derive from the same
        // classification, so the same object can't be "unchanged" and "a regression" at
        // once), and AnsiPen renders it dim. The flipped line itself still surfaces via
        // LineChanges — indirect changes are reported by identity, not magnitude.
        var before = Make(new FileCoverage("a.cs", 19999, 20000, 0, 0) { LineHits = new Dictionary<int, int> { [10] = 1 } });
        var after = Make(new FileCoverage("a.cs", 19998, 20000, 0, 0) { LineHits = new Dictionary<int, int> { [10] = 0 } });
        var result = CoverageDiff.Compare(before, after);
        var d = Assert.ContainsSingle(result.Files);
        Assert.AreEqual(FileChangeKind.Unchanged, d.Change);
        Assert.IsEmpty(result.Regressions);
        Assert.IsEmpty(result.Improvements);
        Assert.ContainsSingle(d.LineChanges);
        Assert.IsExactInstanceOfType<LineDelta.NewlyMissed>(d.LineChanges[0]);
        // The rendered color shares the same epsilon: dim, not red.
        var pen = new AnsiPen(enabled: true);
        Assert.StartsWith("\e[2m", pen.Delta("x", d.Delta));
    }

    [TestMethod]
    public void Regressions_IncludeRemovedFiles_Improvements_IncludeAddedFiles()
    {
        // Losing a measured file is a regression of what the report vouches for; a new
        // covered file is an improvement. Deriving from FileChangeKind must not silently
        // drop the Added/Removed arms.
        var before = Make(new FileCoverage("gone.cs", 8, 10, 0, 0));
        var after = Make(new FileCoverage("fresh.cs", 9, 10, 0, 0));
        var result = CoverageDiff.Compare(before, after);
        Assert.AreEqual("gone.cs", result.Regressions.Single().Path);
        Assert.AreEqual("fresh.cs", result.Improvements.Single().Path);
    }

    [TestMethod]
    public void Compare_DeltaExactlyMovementEpsilon_IsModified()
    {
        // 1/10000 equals the epsilon exactly; only smaller movements count as noise.
        var before = Reports.Single("a.cs", hit: 0, total: 10000);
        var after = Reports.Single("a.cs", hit: 1, total: 10000);
        var result = CoverageDiff.Compare(before, after);
        var file = Assert.ContainsSingle(result.Files);
        Assert.AreEqual(CoverageDiff.MovementEpsilon, file.Delta);
        Assert.AreEqual(FileChangeKind.Modified, file.Change);
    }

    [TestMethod]
    public void Compare_ExactlyTwoSegmentSuffixAgreement_IsAlreadyPairingEvidence()
    {
        // Two matching trailing segments are sufficient; three would miss the boundary.
        var before = Make(new FileCoverage("old/src/App.cs", 1, 2, 0, 0) { LineHits = new Dictionary<int, int> { [1] = 1, [2] = 0 } });
        var after = Make(new FileCoverage("new/src/App.cs", 2, 2, 0, 0) { LineHits = new Dictionary<int, int> { [1] = 1, [2] = 1 } });
        var result = CoverageDiff.Compare(before, after);
        var d = Assert.ContainsSingle(result.Files);
        Assert.AreEqual(FileChangeKind.Modified, d.Change);
        Assert.AreEqual("new/src/App.cs", d.Path);
        Assert.IsEmpty(result.Added);
        Assert.IsEmpty(result.Removed);
    }

    [TestMethod]
    public void Compare_NegativeHits_AreMissed_InEveryView()
    {
        // The parser admits a negative count; the line totals, the line status and the diff all
        // read it as missed, so a never-hit line is not "newly missed".
        var before = Cobertura.NewDoc().AddClass("src/N.cs", c => c.Line(1, hits: -1).Line(2, hits: 1)).Parse();
        var after = Cobertura.NewDoc().AddClass("src/N.cs", c => c.Line(1, hits: 0).Line(2, hits: 1)).Parse();
        var result = CoverageDiff.Compare(before, after);
        var file = before.Files[0];
        Assert.AreEqual(1, file.LinesHit);
        Assert.AreEqual(1, file.StrictlyHitLines);
        Assert.AreEqual(LineStatus.Miss, file.GetLineStatus(1));
        Assert.AreEqual(0, result.TotalLineChanges);
    }
}
