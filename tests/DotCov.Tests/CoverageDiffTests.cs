using Xunit;

namespace DotCov.Tests;

public sealed class CoverageDiffTests
{
    private static CoverageReport Make(params FileCoverage[] files) => new(files);

    [Fact]
    public void Compare_IdenticalReports_AllDeltasZero()
    {
        var report = Make(new FileCoverage("a.cs", 8, 10, 0, 0));
        var result = CoverageDiff.Compare(report, report);

        Assert.Single(result.Files);
        Assert.Equal(0.0, result.Files[0].Delta);
        Assert.Equal(FileChangeKind.Unchanged, result.Files[0].Change);
    }

    [Fact]
    public void Compare_ImprovedCoverage_PositiveDelta()
    {
        var before = Make(new FileCoverage("a.cs", 5, 10, 0, 0));
        var after = Make(new FileCoverage("a.cs", 8, 10, 0, 0));

        var result = CoverageDiff.Compare(before, after);

        Assert.Equal(0.3, result.Files[0].Delta!.Value, precision: 10);
        Assert.Equal(FileChangeKind.Modified, result.Files[0].Change);
    }

    [Fact]
    public void Compare_RegressionInCoverage_NegativeDelta()
    {
        var before = Make(new FileCoverage("a.cs", 9, 10, 0, 0));
        var after = Make(new FileCoverage("a.cs", 6, 10, 0, 0));

        var result = CoverageDiff.Compare(before, after);

        Assert.True(result.Files[0].Delta < 0);
        Assert.Single(result.Regressions);
    }

    [Fact]
    public void Compare_NewFileInAfter_MarkedAsAdded()
    {
        var before = Make();
        var after = Make(new FileCoverage("new.cs", 5, 10, 0, 0));

        var result = CoverageDiff.Compare(before, after);

        Assert.Single(result.Files);
        Assert.Null(result.Files[0].Before);
        Assert.Equal(FileChangeKind.Added, result.Files[0].Change);
        Assert.Single(result.Added);
    }

    [Fact]
    public void Compare_RemovedFile_MarkedAsRemoved()
    {
        var before = Make(new FileCoverage("old.cs", 8, 10, 0, 0));
        var after = Make();

        var result = CoverageDiff.Compare(before, after);

        Assert.Single(result.Files);
        Assert.Null(result.Files[0].After);
        Assert.Equal(FileChangeKind.Removed, result.Files[0].Change);
        Assert.Single(result.Removed);
    }

    [Fact]
    public void Compare_Summary_ReportsOverallDelta()
    {
        var before = Make(new FileCoverage("a.cs", 5, 10, 0, 0));
        var after = Make(new FileCoverage("a.cs", 8, 10, 0, 0));

        var result = CoverageDiff.Compare(before, after);

        Assert.Equal(0.5, result.BeforeRate);
        Assert.Equal(0.8, result.AfterRate);
        Assert.Equal(0.3, result.Delta!.Value, precision: 10);
    }

    [Fact]
    public void Compare_OrdersByDeltaAscending_WorstFirst()
    {
        var before = Make(
            new FileCoverage("good.cs", 5, 10, 0, 0),
            new FileCoverage("bad.cs", 9, 10, 0, 0));
        var after = Make(
            new FileCoverage("good.cs", 9, 10, 0, 0),
            new FileCoverage("bad.cs", 3, 10, 0, 0));

        var result = CoverageDiff.Compare(before, after);

        Assert.Equal("bad.cs", result.Files[0].Path);
        Assert.Equal("good.cs", result.Files[1].Path);
    }

    [Fact]
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

        var d = Assert.Single(result.Files);
        Assert.NotNull(d.Before);
        Assert.Equal(FileChangeKind.Modified, d.Change);
        Assert.Equal("src/App.cs", d.Path);
    }

    [Fact]
    public void Compare_SingleSameNamedPairUnderEqualRoots_StaysRemovedPlusAdded()
    {
        // svc-a/Program.cs deleted while svc-b/Program.cs appears: the names collide but the
        // reports carry no evidence of a path-convention change (roots equal — here both
        // empty — no multi-segment suffix agreement, not a casing drift). Pairing them would
        // fabricate a Modified entry with line changes neither report contains and suppress
        // the honest Removed record; the fallback must leave them apart.
        var before = Make(new FileCoverage("svc-a/Program.cs", 8, 10, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [1] = 1 }
        });
        var after = Make(new FileCoverage("svc-b/Program.cs", 1, 10, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [1] = 0 }
        });

        var result = CoverageDiff.Compare(before, after);

        Assert.Equal(2, result.Files.Count);
        Assert.Equal("svc-a/Program.cs", Assert.Single(result.Removed).Path);
        Assert.Equal("svc-b/Program.cs", Assert.Single(result.Added).Path);
        Assert.DoesNotContain(result.Files, f => f.Change is FileChangeKind.Modified);
        Assert.All(result.Files, f => Assert.Empty(f.LineChanges));
    }

    [Fact]
    public void Compare_MultiSegmentSuffixAgreement_PairsEvenWithoutDeclaredRoots()
    {
        // Hand-built snapshots carry no source roots, so a prefix migration must pair through
        // path evidence alone: two whole trailing segments agree (MyApp/Calculator.cs) —
        // which a bare name collision (svc-a/Program.cs vs svc-b/Program.cs) never satisfies.
        var before = Make(new FileCoverage("src/MyApp/Calculator.cs", 1, 3, 0, 0));
        var after = Make(new FileCoverage("/_/src/MyApp/Calculator.cs", 2, 3, 0, 0));

        var result = CoverageDiff.Compare(before, after);

        var d = Assert.Single(result.Files);
        Assert.Equal(FileChangeKind.Modified, d.Change);
        Assert.Equal("/_/src/MyApp/Calculator.cs", d.Path);
        Assert.Empty(result.Added);
        Assert.Empty(result.Removed);
    }

    [Fact]
    public void Compare_CaseDistinctFileNames_StayDistinctAndMatchExactly()
    {
        // xt_TCPMSS.c and xt_tcpmss.c genuinely coexist (linux/net/netfilter). Under the old
        // OrdinalIgnoreCase lookups this diff couldn't even be built — ToDictionary threw on
        // the "duplicate" keys. Ordinal keying matches each exactly; the file-name fallback
        // never crosses them because final-segment comparison is Ordinal too.
        var before = Make(
            new FileCoverage("net/xt_TCPMSS.c", 4, 4, 0, 0),
            new FileCoverage("net/xt_tcpmss.c", 0, 4, 0, 0));
        var after = Make(
            new FileCoverage("net/xt_TCPMSS.c", 4, 4, 0, 0),
            new FileCoverage("net/xt_tcpmss.c", 2, 4, 0, 0));

        var result = CoverageDiff.Compare(before, after);

        Assert.Equal(2, result.Files.Count);
        var upper = result.Files.Single(f => f.Path == "net/xt_TCPMSS.c");
        var lower = result.Files.Single(f => f.Path == "net/xt_tcpmss.c");
        Assert.Equal(FileChangeKind.Unchanged, upper.Change);
        Assert.Equal(FileChangeKind.Modified, lower.Change);
        Assert.Equal(0.5, lower.Delta);
    }

    [Fact]
    public void Compare_AddedAndRemovedZeroRateFiles_AreNeitherRegressionsNorImprovements()
    {
        // Zero-delta boundary: an added 0%-file has Delta 0.0 and a removed 0%-file has
        // Delta -0.0 — both sit exactly ON the strict inequalities gating Regressions
        // (Delta < 0) and Improvements (Delta > 0). They belong in Added/Removed only.
        var before = Make(new FileCoverage("gone.cs", 0, 2, 0, 0));
        var after = Make(new FileCoverage("fresh.cs", 0, 2, 0, 0));

        var result = CoverageDiff.Compare(before, after);

        Assert.Equal("fresh.cs", Assert.Single(result.Added).Path);
        Assert.Equal("gone.cs", Assert.Single(result.Removed).Path);
        Assert.Empty(result.Regressions);
        Assert.Empty(result.Improvements);
    }

    [Fact]
    public void Compare_LineFlippedFromHitToMiss_SurfacesAsNewlyMissed()
    {
        var before = Make(new FileCoverage("a.cs", 1, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 3 }
        });
        var after = Make(new FileCoverage("a.cs", 0, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 0 }
        });

        var result = CoverageDiff.Compare(before, after);
        var fileDelta = Assert.Single(result.Files);
        var lineDelta = Assert.Single(fileDelta.LineChanges);

        var newlyMissed = Assert.IsType<LineDelta.NewlyMissed>(lineDelta);
        Assert.Equal(10, newlyMissed.Line);
        Assert.Equal(3, newlyMissed.BeforeHits);
        Assert.Equal(0, newlyMissed.AfterHits);
    }

    [Fact]
    public void Compare_LineFlippedFromMissToHit_SurfacesAsNewlyHit()
    {
        var before = Make(new FileCoverage("a.cs", 0, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 0 }
        });
        var after = Make(new FileCoverage("a.cs", 1, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 5 }
        });

        var result = CoverageDiff.Compare(before, after);
        var lineDelta = Assert.Single(result.Files[0].LineChanges);

        var newlyHit = Assert.IsType<LineDelta.NewlyHit>(lineDelta);
        Assert.Equal(0, newlyHit.BeforeHits);
        Assert.Equal(5, newlyHit.AfterHits);
    }

    [Fact]
    public void Compare_AddedAndRemovedLines_AppearWithRespectiveKindsAndPayloads()
    {
        var before = Make(new FileCoverage("a.cs", 1, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 1, [20] = 4 }
        });
        var after = Make(new FileCoverage("a.cs", 1, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 1, [30] = 7 }
        });

        var result = CoverageDiff.Compare(before, after);
        var changes = result.Files[0].LineChanges;

        Assert.Equal(2, changes.Count);

        var removed = Assert.IsType<LineDelta.Removed>(changes.Single(c => c.Line == 20));
        Assert.Equal(4, removed.BeforeHits);

        var added = Assert.IsType<LineDelta.Added>(changes.Single(c => c.Line == 30));
        Assert.Equal(7, added.AfterHits);
    }

    [Fact]
    public void Compare_LineChanges_AreSortedAfterFilteringUnchangedLines()
    {
        var before = Make(new FileCoverage("a.cs", 2, 4, 0, 0)
        {
            LineHits = new Dictionary<int, int>
            {
                [1000] = 1,
                [20] = 4,
                [10] = 0,
                [30] = 1
            }
        });
        var after = Make(new FileCoverage("a.cs", 2, 4, 0, 0)
        {
            LineHits = new Dictionary<int, int>
            {
                [1000] = 1,
                [30] = 0,
                [40] = 7,
                [10] = 5
            }
        });

        var result = CoverageDiff.Compare(before, after);

        Assert.Equal([10, 20, 30, 40], result.Files[0].LineChanges.Select(c => c.Line));
    }

    [Fact]
    public void Compare_UnchangedHitCount_ProducesNoLineChange()
    {
        var both = new FileCoverage("a.cs", 1, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 5 }
        };

        var result = CoverageDiff.Compare(Make(both), Make(both));

        Assert.Empty(result.Files[0].LineChanges);
    }

    [Fact]
    public void Compare_HitCountChangedButStillHit_ProducesNoLineChange()
    {
        var before = Make(new FileCoverage("a.cs", 1, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 100 }
        });
        var after = Make(new FileCoverage("a.cs", 1, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 1 }
        });

        var result = CoverageDiff.Compare(before, after);

        Assert.Empty(result.Files[0].LineChanges);
    }

    [Fact]
    public void CoverageDiffResult_WithLineChanges_FiltersFilesWithFlippedLines()
    {
        var before = Make(
            new FileCoverage("flipped.cs", 1, 1, 0, 0)
            {
                LineHits = new Dictionary<int, int> { [10] = 1 }
            },
            new FileCoverage("stable.cs", 1, 1, 0, 0)
            {
                LineHits = new Dictionary<int, int> { [20] = 1 }
            });
        var after = Make(
            new FileCoverage("flipped.cs", 0, 1, 0, 0)
            {
                LineHits = new Dictionary<int, int> { [10] = 0 }
            },
            new FileCoverage("stable.cs", 1, 1, 0, 0)
            {
                LineHits = new Dictionary<int, int> { [20] = 1 }
            });

        var result = CoverageDiff.Compare(before, after);

        var affected = Assert.Single(result.WithLineChanges);
        Assert.Equal("flipped.cs", affected.Path);
        Assert.Equal(1, result.TotalLineChanges);
    }

    [Fact]
    public void Compare_AddedOrRemovedFile_HasNoLineChanges()
    {
        var before = Make(new FileCoverage("gone.cs", 1, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 1 }
        });
        var after = Make(new FileCoverage("fresh.cs", 1, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [20] = 1 }
        });

        var result = CoverageDiff.Compare(before, after);

        Assert.All(result.Files, f => Assert.Empty(f.LineChanges));
    }

    [Fact]
    public void Compare_LineMissedOnBothSides_ProducesNoLineChange()
    {
        var both = new FileCoverage("a.cs", 0, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 0 }
        };

        var result = CoverageDiff.Compare(Make(both), Make(both));

        Assert.Empty(result.Files[0].LineChanges);
    }

    [Fact]
    public void LineDelta_Match_RoutesEachVariantToItsOwnArm()
    {
        // Match<T> is the value-returning half of the visitor pair; production uses Switch, so
        // nothing else exercises these four copy-paste-shaped overrides — pin that each routes to
        // its own arm (a mis-wire like Added.Match -> removed(this) would otherwise go unnoticed).
        string Tag(LineDelta d) => d.Match(
            added:       _ => "added",
            removed:     _ => "removed",
            newlyHit:    _ => "newlyHit",
            newlyMissed: _ => "newlyMissed");

        Assert.Equal("added", Tag(new LineDelta.Added(1, 5)));
        Assert.Equal("removed", Tag(new LineDelta.Removed(2, 3)));
        Assert.Equal("newlyHit", Tag(new LineDelta.NewlyHit(3, 0, 4)));
        Assert.Equal("newlyMissed", Tag(new LineDelta.NewlyMissed(4, 7, 0)));
    }

    [Fact]
    public void Compare_UnmeasuredOnBothSides_IsUnchangedWithNullDelta()
    {
        // A file both reports list but neither measured has no rates to compare: unmeasured
        // on both ends is unchanged, not modified — and the delta is null, not 0.
        var before = Make(new FileCoverage("empty.cs", 0, 0, 0, 0));
        var after = Make(new FileCoverage("empty.cs", 0, 0, 0, 0));

        var result = CoverageDiff.Compare(before, after);

        var d = Assert.Single(result.Files);
        Assert.Equal(FileChangeKind.Unchanged, d.Change);
        Assert.Null(d.Delta);
        Assert.Empty(result.Regressions);
        Assert.Empty(result.Improvements);
    }

    [Fact]
    public void Compare_SubEpsilonDelta_IsUnchangedEverywhere_ButLineChangesStillSurface()
    {
        // One line flipping in a 20,000-line file moves the rate by 0.00005 — inside
        // MovementEpsilon. Every movement view must agree it's noise: Change is Unchanged,
        // the file is in NEITHER Regressions nor Improvements (they derive from the same
        // classification, so the same object can't be "unchanged" and "a regression" at
        // once), and AnsiPen renders it dim. The flipped line itself still surfaces via
        // LineChanges — indirect changes are reported by identity, not magnitude.
        var before = Make(new FileCoverage("a.cs", 19999, 20000, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 1 }
        });
        var after = Make(new FileCoverage("a.cs", 19998, 20000, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 0 }
        });

        var result = CoverageDiff.Compare(before, after);

        var d = Assert.Single(result.Files);
        Assert.Equal(FileChangeKind.Unchanged, d.Change);
        Assert.Empty(result.Regressions);
        Assert.Empty(result.Improvements);
        Assert.Single(d.LineChanges);
        Assert.IsType<LineDelta.NewlyMissed>(d.LineChanges[0]);

        // The rendered color shares the same epsilon: dim, not red.
        var pen = new DotCov.Formatters.AnsiPen(enabled: true);
        Assert.StartsWith("\e[2m", pen.Delta("x", d.Delta));
    }

    [Fact]
    public void Regressions_IncludeRemovedFiles_Improvements_IncludeAddedFiles()
    {
        // Losing a measured file is a regression of what the report vouches for; a new
        // covered file is an improvement. Deriving from FileChangeKind must not silently
        // drop the Added/Removed arms.
        var before = Make(new FileCoverage("gone.cs", 8, 10, 0, 0));
        var after = Make(new FileCoverage("fresh.cs", 9, 10, 0, 0));

        var result = CoverageDiff.Compare(before, after);

        Assert.Equal("gone.cs", Assert.Single(result.Regressions).Path);
        Assert.Equal("fresh.cs", Assert.Single(result.Improvements).Path);
    }
}
