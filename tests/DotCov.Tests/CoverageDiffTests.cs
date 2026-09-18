using DotCov.Formatters;
using DotCov.Tests.Infrastructure;
using TUnit.Assertions.Enums;

namespace DotCov.Tests;

public sealed class CoverageDiffTests
{
    private static CoverageReport Make(params FileCoverage[] files) => new(files);

    [Test]
    public async Task Compare_IdenticalReports_AllDeltasZero()
    {
        var report = Make(new FileCoverage("a.cs", 8, 10, 0, 0));
        var result = CoverageDiff.Compare(report, report);

        await Assert.That(result.Files).HasSingleItem();
        await Assert.That(result.Files[0].Delta).IsEqualTo(0.0);
        await Assert.That(result.Files[0].Change).IsEqualTo(FileChangeKind.Unchanged);
    }

    [Test]
    public async Task Compare_ImprovedCoverage_PositiveDelta()
    {
        var before = Make(new FileCoverage("a.cs", 5, 10, 0, 0));
        var after = Make(new FileCoverage("a.cs", 8, 10, 0, 0));

        var result = CoverageDiff.Compare(before, after);

        await Assert.That(result.Files[0].Delta!.Value).IsEqualTo(0.3).Within(1e-10);
        await Assert.That(result.Files[0].Change).IsEqualTo(FileChangeKind.Modified);
    }

    [Test]
    public async Task Compare_RegressionInCoverage_NegativeDelta()
    {
        var before = Make(new FileCoverage("a.cs", 9, 10, 0, 0));
        var after = Make(new FileCoverage("a.cs", 6, 10, 0, 0));

        var result = CoverageDiff.Compare(before, after);

        await Assert.That(result.Files[0].Delta < 0).IsTrue();
        await Assert.That(result.Regressions).HasSingleItem();
    }

    [Test]
    public async Task Compare_NewFileInAfter_MarkedAsAdded()
    {
        var before = Make();
        var after = Make(new FileCoverage("new.cs", 5, 10, 0, 0));

        var result = CoverageDiff.Compare(before, after);

        await Assert.That(result.Files).HasSingleItem();
        await Assert.That(result.Files[0].Before).IsNull();
        await Assert.That(result.Files[0].Change).IsEqualTo(FileChangeKind.Added);
        await Assert.That(result.Added).HasSingleItem();
    }

    [Test]
    public async Task Compare_RemovedFile_MarkedAsRemoved()
    {
        var before = Make(new FileCoverage("old.cs", 8, 10, 0, 0));
        var after = Make();

        var result = CoverageDiff.Compare(before, after);

        await Assert.That(result.Files).HasSingleItem();
        await Assert.That(result.Files[0].After).IsNull();
        await Assert.That(result.Files[0].Change).IsEqualTo(FileChangeKind.Removed);
        await Assert.That(result.Removed).HasSingleItem();
    }

    [Test]
    public async Task Compare_Summary_ReportsOverallDelta()
    {
        var before = Make(new FileCoverage("a.cs", 5, 10, 0, 0));
        var after = Make(new FileCoverage("a.cs", 8, 10, 0, 0));

        var result = CoverageDiff.Compare(before, after);

        await Assert.That(result.BeforeRate).IsEqualTo(0.5);
        await Assert.That(result.AfterRate).IsEqualTo(0.8);
        await Assert.That(result.Delta!.Value).IsEqualTo(0.3).Within(1e-10);
    }

    [Test]
    public async Task Compare_OrdersByDeltaAscending_WorstFirst()
    {
        var before = Make(
            new FileCoverage("good.cs", 5, 10, 0, 0),
            new FileCoverage("bad.cs", 9, 10, 0, 0));
        var after = Make(
            new FileCoverage("good.cs", 9, 10, 0, 0),
            new FileCoverage("bad.cs", 3, 10, 0, 0));

        var result = CoverageDiff.Compare(before, after);

        await Assert.That(result.Files[0].Path).IsEqualTo("bad.cs");
        await Assert.That(result.Files[1].Path).IsEqualTo("good.cs");
    }

    [Test]
    public async Task Compare_DirectoryCaseDrift_PairsViaUniqueFileNameFallback()
    {
        // Exact path matching is Ordinal (case-differing paths are distinct files on the
        // case-sensitive filesystems Cobertura's native emitters run on). A directory-casing
        // drift between two uploads of the same file still pairs — through the unique
        // file-name fallback, since 'App.cs' is carried by exactly one leftover file on each
        // side — instead of reading as removed+added.
        var before = Make(new FileCoverage("SRC/App.cs", 5, 10, 0, 0));
        var after = Make(new FileCoverage("src/App.cs", 8, 10, 0, 0));

        var result = CoverageDiff.Compare(before, after);

        var d = await Assert.That(result.Files).HasSingleItem();
        await Assert.That(d.Before).IsNotNull();
        await Assert.That(d.Change).IsEqualTo(FileChangeKind.Modified);
        await Assert.That(d.Path).IsEqualTo("src/App.cs");
    }

    [Test]
    public async Task Compare_SingleSameNamedPairUnderEqualRoots_StaysRemovedPlusAdded()
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

        await Assert.That(result.Files.Count).IsEqualTo(2);
        await Assert.That(result.Removed.Single().Path).IsEqualTo("svc-a/Program.cs");
        await Assert.That(result.Added.Single().Path).IsEqualTo("svc-b/Program.cs");
        await Assert.That(result.Files).DoesNotContain(f => f.Change is FileChangeKind.Modified);
        foreach (var f in result.Files) await Assert.That(f.LineChanges).IsEmpty();
    }

    [Test]
    public async Task Compare_MultiSegmentSuffixAgreement_PairsEvenWithoutDeclaredRoots()
    {
        // Hand-built snapshots carry no source roots, so a prefix migration must pair through
        // path evidence alone: two whole trailing segments agree (MyApp/Calculator.cs) —
        // which a bare name collision (svc-a/Program.cs vs svc-b/Program.cs) never satisfies.
        var before = Make(new FileCoverage("src/MyApp/Calculator.cs", 1, 3, 0, 0));
        var after = Make(new FileCoverage("/_/src/MyApp/Calculator.cs", 2, 3, 0, 0));

        var result = CoverageDiff.Compare(before, after);

        var d = await Assert.That(result.Files).HasSingleItem();
        await Assert.That(d.Change).IsEqualTo(FileChangeKind.Modified);
        await Assert.That(d.Path).IsEqualTo("/_/src/MyApp/Calculator.cs");
        await Assert.That(result.Added).IsEmpty();
        await Assert.That(result.Removed).IsEmpty();
    }

    [Test]
    public async Task Compare_CaseDistinctFileNames_StayDistinctAndMatchExactly()
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

        await Assert.That(result.Files.Count).IsEqualTo(2);
        var upper = result.Files.Single(f => f.Path == "net/xt_TCPMSS.c");
        var lower = result.Files.Single(f => f.Path == "net/xt_tcpmss.c");
        await Assert.That(upper.Change).IsEqualTo(FileChangeKind.Unchanged);
        await Assert.That(lower.Change).IsEqualTo(FileChangeKind.Modified);
        await Assert.That(lower.Delta).IsEqualTo(0.5);
    }

    [Test]
    public async Task Compare_RemovedZeroRateFile_IsARegression_AddedZeroRateFile_IsNotAnImprovement()
    {
        // Variant A: losing a MEASURED file is a regression of what the report vouches for,
        // whatever its rate — a removed 0% file included. Its numeric delta is positive zero
        // (never -0.0, which would render as "+-0.0%"). A new 0% file adds nothing that could
        // count as an improvement, and is not a regression either.
        var before = Make(new FileCoverage("gone.cs", 0, 2, 0, 0));
        var after = Make(new FileCoverage("fresh.cs", 0, 2, 0, 0));

        var result = CoverageDiff.Compare(before, after);

        var removed = result.Removed.Single();
        await Assert.That(removed.Path).IsEqualTo("gone.cs");
        await Assert.That(removed.Delta).IsEqualTo(0.0);
        await Assert.That(double.IsNegative(removed.Delta!.Value)).IsFalse();
        await Assert.That(removed.IsRegression).IsTrue();
        await Assert.That(removed.IsImprovement).IsFalse();
        await Assert.That(result.Regressions.Single().Path).IsEqualTo("gone.cs");

        var added = result.Added.Single();
        await Assert.That(added.Path).IsEqualTo("fresh.cs");
        await Assert.That(added.Delta).IsEqualTo(0.0);
        await Assert.That(added.IsRegression).IsFalse();
        await Assert.That(added.IsImprovement).IsFalse();
        await Assert.That(result.Improvements).IsEmpty();
    }

    [Test]
    public async Task FormatDiff_RemovedZeroRateFile_NeverRendersADoubleSign()
    {
        // Control: a removed file with a positive rate renders its own '-'; the removed 0%
        // file renders "+0.0%" — the sign policy is "+ for >= 0", so the delta itself must be
        // positive zero rather than the text being patched afterwards.
        var before = Make(
            new FileCoverage("gone.cs", 0, 2, 0, 0),
            new FileCoverage("ctrl.cs", 8, 10, 0, 0));
        var after = Make();

        var result = CoverageDiff.Compare(before, after);
        var table = AnsiStrip.From(TableFormatter.FormatDiff(result));
        var md = MarkdownFormatter.FormatDiff(result);

        await Assert.That(table).DoesNotContain("+-");
        await Assert.That(md).DoesNotContain("+-");
        await Assert.That(table).Matches(@"gone\.cs\s+0\.0%\s+-\s+\+0\.0%\s+Removed");
        await Assert.That(table).Matches(@"ctrl\.cs\s+80\.0%\s+-\s+-80\.0%\s+Removed");
        await Assert.That(md).Contains("| `gone.cs` | 0.0% | - | +0.0% | Removed |");
        await Assert.That(md).Contains("| `ctrl.cs` | 80.0% | - | -80.0% | Removed |");
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

    [Test]
    [MethodDataSource(nameof(ClassificationCases))]
    public async Task FileDelta_ClassificationTable_SingleFlagsAndResultFiltersAgree(
        string scenario, FileCoverage? before, FileCoverage? after, double? delta, FileChangeKind change, bool regression, bool improvement)
    {
        var result = CoverageDiff.Compare(
            before is { } b ? Make(b) : Make(),
            after is { } a ? Make(a) : Make());

        var d = await Assert.That(result.Files).HasSingleItem();
        await Assert.That(d.Change).IsEqualTo(change).Because(scenario);
        if (delta is { } expected)
        {
            await Assert.That(d.Delta).IsNotNull().Because(scenario);
            await Assert.That(d.Delta!.Value).IsEqualTo(expected).Within(1e-12).Because(scenario);
            await Assert.That(double.IsNegative(d.Delta.Value) && d.Delta.Value == 0).IsFalse().Because($"{scenario}: no negative zero");
        }
        else
        {
            await Assert.That(d.Delta).IsNull().Because(scenario);
        }

        await Assert.That(d.IsRegression).IsEqualTo(regression).Because(scenario);
        await Assert.That(d.IsImprovement).IsEqualTo(improvement).Because(scenario);
        await Assert.That(result.Regressions.Any()).IsEqualTo(regression).Because($"{scenario}: Regressions filter");
        await Assert.That(result.Improvements.Any()).IsEqualTo(improvement).Because($"{scenario}: Improvements filter");
    }

    [Test]
    public async Task Compare_LineFlippedFromHitToMiss_SurfacesAsNewlyMissed()
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
        var fileDelta = await Assert.That(result.Files).HasSingleItem();
        var lineDelta = await Assert.That(fileDelta.LineChanges).HasSingleItem();

        var newlyMissed = (await Assert.That(lineDelta).IsTypeOf<LineDelta.NewlyMissed>())!;
        await Assert.That(newlyMissed.Line).IsEqualTo(10);
        await Assert.That(newlyMissed.BeforeHits).IsEqualTo(3);
        await Assert.That(newlyMissed.AfterHits).IsEqualTo(0);
    }

    [Test]
    public async Task Compare_LineFlippedFromMissToHit_SurfacesAsNewlyHit()
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
        var lineDelta = await Assert.That(result.Files[0].LineChanges).HasSingleItem();

        var newlyHit = (await Assert.That(lineDelta).IsTypeOf<LineDelta.NewlyHit>())!;
        await Assert.That(newlyHit.BeforeHits).IsEqualTo(0);
        await Assert.That(newlyHit.AfterHits).IsEqualTo(5);
    }

    [Test]
    public async Task Compare_AddedAndRemovedLines_AppearWithRespectiveKindsAndPayloads()
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

        await Assert.That(changes.Count).IsEqualTo(2);

        var removed = (await Assert.That(changes.Single(c => c.Line == 20)).IsTypeOf<LineDelta.Removed>())!;
        await Assert.That(removed.BeforeHits).IsEqualTo(4);

        var added = (await Assert.That(changes.Single(c => c.Line == 30)).IsTypeOf<LineDelta.Added>())!;
        await Assert.That(added.AfterHits).IsEqualTo(7);
    }

    [Test]
    public async Task Compare_LineChanges_AreSortedAfterFilteringUnchangedLines()
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

        await Assert.That(result.Files[0].LineChanges.Select(c => c.Line)).IsEquivalentTo([10, 20, 30, 40], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Compare_UnchangedHitCount_ProducesNoLineChange()
    {
        var both = new FileCoverage("a.cs", 1, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 5 }
        };

        var result = CoverageDiff.Compare(Make(both), Make(both));

        await Assert.That(result.Files[0].LineChanges).IsEmpty();
    }

    [Test]
    public async Task Compare_HitCountChangedButStillHit_ProducesNoLineChange()
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

        await Assert.That(result.Files[0].LineChanges).IsEmpty();
    }

    [Test]
    public async Task CoverageDiffResult_WithLineChanges_FiltersFilesWithFlippedLines()
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

        var affected = await Assert.That(result.WithLineChanges).HasSingleItem();
        await Assert.That(affected.Path).IsEqualTo("flipped.cs");
        await Assert.That(result.TotalLineChanges).IsEqualTo(1);
    }

    [Test]
    public async Task Compare_AddedOrRemovedFile_HasNoLineChanges()
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

        foreach (var f in result.Files) await Assert.That(f.LineChanges).IsEmpty();
    }

    [Test]
    public async Task Compare_LineMissedOnBothSides_ProducesNoLineChange()
    {
        var both = new FileCoverage("a.cs", 0, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 0 }
        };

        var result = CoverageDiff.Compare(Make(both), Make(both));

        await Assert.That(result.Files[0].LineChanges).IsEmpty();
    }

    [Test]
    public async Task LineDelta_Match_RoutesEachVariantToItsOwnArm()
    {
        // Match<T> is the value-returning half of the visitor pair; production uses Switch, so
        // nothing else exercises these four copy-paste-shaped overrides — pin that each routes to
        // its own arm (a mis-wire like Added.Match -> removed(this) would otherwise go unnoticed).
        string Tag(LineDelta d) => d.Match(
            added:       _ => "added",
            removed:     _ => "removed",
            newlyHit:    _ => "newlyHit",
            newlyMissed: _ => "newlyMissed");

        await Assert.That(Tag(new LineDelta.Added(1, 5))).IsEqualTo("added");
        await Assert.That(Tag(new LineDelta.Removed(2, 3))).IsEqualTo("removed");
        await Assert.That(Tag(new LineDelta.NewlyHit(3, 0, 4))).IsEqualTo("newlyHit");
        await Assert.That(Tag(new LineDelta.NewlyMissed(4, 7, 0))).IsEqualTo("newlyMissed");
    }

    [Test]
    public async Task Compare_UnmeasuredOnBothSides_IsUnchangedWithNullDelta()
    {
        // A file both reports list but neither measured has no rates to compare: unmeasured
        // on both ends is unchanged, not modified — and the delta is null, not 0.
        var before = Make(new FileCoverage("empty.cs", 0, 0, 0, 0));
        var after = Make(new FileCoverage("empty.cs", 0, 0, 0, 0));

        var result = CoverageDiff.Compare(before, after);

        var d = await Assert.That(result.Files).HasSingleItem();
        await Assert.That(d.Change).IsEqualTo(FileChangeKind.Unchanged);
        await Assert.That(d.Delta).IsNull();
        await Assert.That(result.Regressions).IsEmpty();
        await Assert.That(result.Improvements).IsEmpty();
    }

    [Test]
    public async Task Compare_SubEpsilonDelta_IsUnchangedEverywhere_ButLineChangesStillSurface()
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

        var d = await Assert.That(result.Files).HasSingleItem();
        await Assert.That(d.Change).IsEqualTo(FileChangeKind.Unchanged);
        await Assert.That(result.Regressions).IsEmpty();
        await Assert.That(result.Improvements).IsEmpty();
        await Assert.That(d.LineChanges).HasSingleItem();
        await Assert.That(d.LineChanges[0]).IsTypeOf<LineDelta.NewlyMissed>();

        // The rendered color shares the same epsilon: dim, not red.
        var pen = new DotCov.Formatters.AnsiPen(enabled: true);
        await Assert.That(pen.Delta("x", d.Delta)).StartsWith("\e[2m");
    }

    [Test]
    public async Task Regressions_IncludeRemovedFiles_Improvements_IncludeAddedFiles()
    {
        // Losing a measured file is a regression of what the report vouches for; a new
        // covered file is an improvement. Deriving from FileChangeKind must not silently
        // drop the Added/Removed arms.
        var before = Make(new FileCoverage("gone.cs", 8, 10, 0, 0));
        var after = Make(new FileCoverage("fresh.cs", 9, 10, 0, 0));

        var result = CoverageDiff.Compare(before, after);

        await Assert.That(result.Regressions.Single().Path).IsEqualTo("gone.cs");
        await Assert.That(result.Improvements.Single().Path).IsEqualTo("fresh.cs");
    }

    [Test]
    public async Task Compare_DeltaExactlyMovementEpsilon_IsModified()
    {
        // 1/10000 equals the epsilon exactly; only smaller movements count as noise.
        var before = Reports.Single("a.cs", hit: 0, total: 10000);
        var after = Reports.Single("a.cs", hit: 1, total: 10000);

        var result = CoverageDiff.Compare(before, after);

        var file = await Assert.That(result.Files).HasSingleItem();
        await Assert.That(file.Delta).IsEqualTo(CoverageDiff.MovementEpsilon);
        await Assert.That(file.Change).IsEqualTo(FileChangeKind.Modified);
    }

    [Test]
    public async Task Compare_ExactlyTwoSegmentSuffixAgreement_IsAlreadyPairingEvidence()
    {
        // Two matching trailing segments are sufficient; three would miss the boundary.
        var before = Make(new FileCoverage("old/src/App.cs", 1, 2, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [1] = 1, [2] = 0 }
        });
        var after = Make(new FileCoverage("new/src/App.cs", 2, 2, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [1] = 1, [2] = 1 }
        });

        var result = CoverageDiff.Compare(before, after);

        var d = await Assert.That(result.Files).HasSingleItem();
        await Assert.That(d.Change).IsEqualTo(FileChangeKind.Modified);
        await Assert.That(d.Path).IsEqualTo("new/src/App.cs");
        await Assert.That(result.Added).IsEmpty();
        await Assert.That(result.Removed).IsEmpty();
    }
}
