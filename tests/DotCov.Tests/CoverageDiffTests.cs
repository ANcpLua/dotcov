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

        Assert.Equal(0.3, result.Files[0].Delta, precision: 10);
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
        Assert.Equal(0.3, result.Delta, precision: 10);
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
    public void Compare_CaseInsensitivePaths_MatchesCorrectly()
    {
        var before = Make(new FileCoverage("SRC/App.cs", 5, 10, 0, 0));
        var after = Make(new FileCoverage("src/App.cs", 8, 10, 0, 0));

        var result = CoverageDiff.Compare(before, after);

        Assert.Single(result.Files);
        Assert.NotNull(result.Files[0].Before);
    }

    // ── Codecov-style indirect coverage changes (line-level diff) ──

    [Fact]
    public void Compare_LineFlippedFromHitToMiss_SurfacesAsNewlyMissed()
    {
        // Same file on both sides; line 10 was hit before, missed after — the canonical
        // "tests were removed / something upstream changed" indirect coverage change.
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

        // Pin the LineDelta payload, not just the variant — guards against parameter-order
        // regressions in ComputeLineChanges where Before/After hit counts could swap.
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

        // Removed: variant carries the dropped hit count; no AfterHits field exists on the
        // type at all — that's the compile-time invariant the sealed-hierarchy enforces.
        var removed = Assert.IsType<LineDelta.Removed>(changes.Single(c => c.Line == 20));
        Assert.Equal(4, removed.BeforeHits);

        // Added: variant carries the new hit count; no BeforeHits field on the type.
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
        // 100 hits → 1 hit is informative but not a state flip; Codecov treats this the
        // same way (hit/miss boolean, not delta on hit count).
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
        // Added/Removed file deltas don't get line-level breakdowns — the whole file is
        // either gone or new, so individual line classifications are redundant.
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
        // Both reports track the same line at zero hits — covered-by-test-suite state didn't
        // change. The line classifier must not emit a delta for it (otherwise every uncovered
        // line in every file would spam "indirect changes" output).
        var both = new FileCoverage("a.cs", 0, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 0 }
        };

        var result = CoverageDiff.Compare(Make(both), Make(both));

        Assert.Empty(result.Files[0].LineChanges);
    }
}
