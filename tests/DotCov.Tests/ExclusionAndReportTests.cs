using DotCov.Tests.Infrastructure;
using Xunit;

namespace DotCov.Tests;

public sealed class ExclusionAndReportTests
{
    [Fact]
    public void WellKnown_ContainsExpectedPatterns()
    {
        Assert.Contains(".g.cs", ExclusionRules.WellKnown);
        Assert.Contains(".designer.cs", ExclusionRules.WellKnown);
        Assert.Contains("/obj/", ExclusionRules.WellKnown);
        Assert.Contains("/bin/", ExclusionRules.WellKnown);
        Assert.Contains("/Migrations/", ExclusionRules.WellKnown);
        Assert.Contains("GlobalUsings", ExclusionRules.WellKnown);
        // No "d__" rule: it matched on Path (the filename), but coverlet only ever puts d__ in the
        // *class name* — so the rule was structurally dead. Assert it's gone, not present.
        Assert.DoesNotContain("d__", ExclusionRules.WellKnown);
        Assert.Contains("/Program.cs", ExclusionRules.WellKnown);
    }

    [Fact]
    public void Exclude_WithKeep_RestoresFilesMatchingKeepPattern()
    {
        // `Program.cs` is in WellKnown, but a CLI tool whose entire surface lives there
        // can opt back in with `--keep Program.cs`.
        var report = new CoverageReport([
            new FileCoverage("src/MyApp/Program.cs", 10, 12, 0, 0),
            new FileCoverage("src/MyApp/Service.cs", 8, 10, 0, 0),
            new FileCoverage("src/MyApp/obj/Generated.cs", 1, 1, 0, 0)
        ]);

        var filtered = report.Exclude(ExclusionRules.WellKnown, keep: ["Program.cs"]);

        Assert.Contains(filtered.Files, f => f.Path.EndsWith("Program.cs"));    // exempted
        Assert.Contains(filtered.Files, f => f.Path.EndsWith("Service.cs"));    // never excluded
        Assert.DoesNotContain(filtered.Files, f => f.Path.Contains("/obj/"));   // still excluded
    }

    [Fact]
    public void Exclude_KeepNotMatched_LeavesExclusionInPlace()
    {
        var report = new CoverageReport([
            new FileCoverage("src/MyApp/Program.cs", 10, 12, 0, 0)
        ]);

        var filtered = report.Exclude(ExclusionRules.WellKnown, keep: ["Worker.cs"]);

        Assert.Empty(filtered.Files);
    }

    [Fact]
    public void Exclude_EmptyPatterns_ReturnsSameInstance()
    {
        var report = Reports.Mixed;

        var filtered = report.Exclude([]);

        Assert.Same(report, filtered);
    }

    [Theory]
    [InlineData("src/MyApp/obj/Debug/file.cs")]
    [InlineData("src/MyApp/OBJ/Debug/file.cs")]
    [InlineData("src/MyApp/bin/Release/file.cs")]
    [InlineData("src/Foo.g.cs")]
    [InlineData("src/Form.Designer.cs")]
    [InlineData("src/MyApp/Migrations/20230101_Init.cs")]
    [InlineData("src/GlobalUsings.cs")]
    public void Exclude_WellKnown_RemovesMatchingFile(string path)
    {
        var report = new CoverageReport([
            new FileCoverage(path, 1, 1, 0, 0),
            new FileCoverage("KeepThis.cs", 1, 1, 0, 0)
        ]);

        var filtered = report.Exclude(ExclusionRules.WellKnown);

        Assert.DoesNotContain(filtered.Files, f => f.Path == path);
    }

    [Theory]
    [InlineData("Source/file.cs")]
    [InlineData("src/MyService.cs")]
    public void Exclude_WellKnown_KeepsNonMatchingFile(string path)
    {
        var report = new CoverageReport([
            new FileCoverage(path, 1, 1, 0, 0),
            new FileCoverage("KeepThis.cs", 1, 1, 0, 0)
        ]);

        var filtered = report.Exclude(ExclusionRules.WellKnown);

        Assert.Contains(filtered.Files, f => f.Path == path);
    }

    [Fact]
    public void Exclude_DoesNotMutateSource()
    {
        var report = Reports.Mixed;
        var originalCount = report.Files.Count;

        _ = report.Exclude(["Unused"]);

        Assert.Equal(originalCount, report.Files.Count);
    }

    [Fact]
    public void Empty_StaticInstance_HasNoFilesAndNoRates()
    {
        // The empty report is the whole point of this change: it used to claim 1.0/1.0, so a
        // glob that matched nothing rendered as flawless coverage and cleared every threshold.
        Assert.Empty(CoverageReport.Empty.Files);
        Assert.Null(CoverageReport.Empty.LineRate);
        Assert.Null(CoverageReport.Empty.BranchRate);
        Assert.False(CoverageReport.Empty.HasLineData);
        Assert.False(CoverageReport.Empty.HasBranchData);
    }

    [Fact]
    public void HasBranchData_FileWithBranches_True()
    {
        var file = new FileCoverage("a.cs", 1, 2, 1, 2);
        Assert.True(file.HasBranchData);
    }

    [Fact]
    public void HasBranchData_FileWithoutBranches_False()
    {
        var file = new FileCoverage("a.cs", 1, 2, 0, 0);
        Assert.False(file.HasBranchData);
    }

    [Fact]
    public void HasBranchData_ReportWithoutAnyBranches_False()
    {
        Assert.False(Reports.LinesOnly.HasBranchData);
        Assert.True(Reports.Mixed.HasBranchData);
    }

    [Fact]
    public void FileCoverage_MergeWith_UnionsLinesAndAppendsPartialBranches()
    {
        // `a` covers line 5, misses 10, has a partial branch on line 15 (1/2).
        // `b` covers lines 20 and 30, misses 25, has a partial branch on line 25 (0/2).
        // Merging keeps the highest hit count per line and derives PartialBranches from the
        // unioned BranchesByLine dict (disjoint partial-branch lines → both surface).
        var a = Reports.ClassifiedFile("a.cs", linesHit: 1, linesTotal: 2, branchesHit: 1, branchesTotal: 2,
            lineHits: new Dictionary<int, int> { [5] = 3, [10] = 0 },
            branchesByLine: new Dictionary<int, (int Covered, int Total)> { [15] = (1, 2) });
        var b = Reports.ClassifiedFile("a.cs", linesHit: 2, linesTotal: 3, branchesHit: 0, branchesTotal: 2,
            lineHits: new Dictionary<int, int> { [20] = 1, [25] = 0, [30] = 1 },
            branchesByLine: new Dictionary<int, (int Covered, int Total)> { [25] = (0, 2) });

        var (merged, _) = a.MergeWith(b);

        Assert.Equal([10, 25], merged.UncoveredLines);
        Assert.Equal(2, merged.PartialBranches.Count);
        Assert.Equal(3, merged.LinesHit);      // 5, 20, 30
        Assert.Equal(5, merged.LinesTotal);    // 5, 10, 20, 25, 30
        Assert.Equal(1, merged.BranchesHit);   // 1 (line 15) + 0 (line 25)
        Assert.Equal(4, merged.BranchesTotal); // 2 + 2
    }

    [Fact]
    public void FileCoverage_MergeWith_BranchesOnlyInOther_PreservesEntry()
    {
        // `a` has the same line tracked but no branch data for it; `b` carries the branches.
        // The merge must keep `b`'s BranchesByLine entry — a regression that overwrote with
        // the empty dict would silently flip the merged line from Partial back to Hit.
        var a = Reports.ClassifiedFile("a.cs", 1, 1, 0, 0,
            lineHits: new Dictionary<int, int> { [10] = 1 },
            branchesByLine: new Dictionary<int, (int Covered, int Total)>());
        var b = Reports.ClassifiedFile("a.cs", 1, 1, 1, 2,
            lineHits: new Dictionary<int, int> { [10] = 1 },
            branchesByLine: new Dictionary<int, (int Covered, int Total)> { [10] = (1, 2) });

        var (merged, _) = a.MergeWith(b);

        Assert.Equal((1, 2), merged.BranchesByLine[10]);
        Assert.Equal(LineStatus.Partial, merged.GetLineStatus(10));
    }

    // ── Codecov-style strict line classification (Hit / Partial / Miss) ──

    [Fact]
    public void GetLineStatus_FullyCoveredLineWithoutBranches_IsHit()
    {
        var f = Reports.ClassifiedFile("a.cs", 1, 1, 0, 0,
            lineHits: new Dictionary<int, int> { [10] = 3 },
            branchesByLine: new Dictionary<int, (int Covered, int Total)>());

        Assert.Equal(LineStatus.Hit, f.GetLineStatus(10));
    }

    [Fact]
    public void GetLineStatus_FullyCoveredLineWithAllBranchesExercised_IsHit()
    {
        var f = Reports.ClassifiedFile("a.cs", 1, 1, 2, 2,
            lineHits: new Dictionary<int, int> { [10] = 3 },
            branchesByLine: new Dictionary<int, (int Covered, int Total)> { [10] = (2, 2) });

        Assert.Equal(LineStatus.Hit, f.GetLineStatus(10));
    }

    [Fact]
    public void GetLineStatus_ExecutedLineWithIncompleteBranches_IsPartial()
    {
        var f = Reports.ClassifiedFile("a.cs", 1, 1, 1, 2,
            lineHits: new Dictionary<int, int> { [10] = 3 },
            branchesByLine: new Dictionary<int, (int Covered, int Total)> { [10] = (1, 2) });

        Assert.Equal(LineStatus.Partial, f.GetLineStatus(10));
    }

    [Fact]
    public void GetLineStatus_ZeroHits_IsMissEvenWithBranchData()
    {
        var f = Reports.ClassifiedFile("a.cs", 0, 1, 0, 2,
            lineHits: new Dictionary<int, int> { [10] = 0 },
            branchesByLine: new Dictionary<int, (int Covered, int Total)> { [10] = (0, 2) });

        Assert.Equal(LineStatus.Miss, f.GetLineStatus(10));
    }

    [Fact]
    public void GetLineStatus_UnknownLine_IsMiss()
    {
        var f = new FileCoverage("a.cs", 0, 0, 0, 0);

        Assert.Equal(LineStatus.Miss, f.GetLineStatus(999));
    }

    [Fact]
    public void TryGetLineStatus_TrackedHitLine_ReturnsTrueAndHit()
    {
        var f = new FileCoverage("a.cs", 1, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 3 }
        };

        Assert.True(f.TryGetLineStatus(10, out var status));
        Assert.Equal(LineStatus.Hit, status);
    }

    [Fact]
    public void TryGetLineStatus_TrackedZeroHitLine_ReturnsTrueAndMiss()
    {
        // The key contract: line IS tracked but had zero hits. Distinct from untracked.
        var f = new FileCoverage("a.cs", 0, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 0 }
        };

        Assert.True(f.TryGetLineStatus(10, out var status));
        Assert.Equal(LineStatus.Miss, status);
    }

    [Fact]
    public void TryGetLineStatus_TrackedPartialBranchLine_ReturnsTrueAndPartial()
    {
        var f = new FileCoverage("a.cs", 1, 1, 1, 2)
        {
            LineHits = new Dictionary<int, int> { [10] = 3 },
            BranchesByLine = new Dictionary<int, (int Covered, int Total)> { [10] = (1, 2) }
        };

        Assert.True(f.TryGetLineStatus(10, out var status));
        Assert.Equal(LineStatus.Partial, status);
    }

    [Fact]
    public void TryGetLineStatus_UnknownLine_ReturnsFalseAndMiss()
    {
        // The other key contract: false signals "not tracked", out param is Miss for ergonomics.
        var f = new FileCoverage("a.cs", 0, 0, 0, 0);

        Assert.False(f.TryGetLineStatus(999, out var status));
        Assert.Equal(LineStatus.Miss, status);
    }

    [Fact]
    public void StrictLineRate_DowngradesPartialBranches()
    {
        // 3 lines tracked: line 1 fully hit (no branches), line 2 hit but with partial branches,
        // line 3 missed. Standard LineRate = 2/3 ≈ 66.7% (lines 1 and 2 are "hit").
        // Strict LineRate = 1/3 ≈ 33.3% (only line 1 is fully Hit; line 2 is Partial, line 3 is Miss).
        var f = Reports.ClassifiedFile("a.cs", 2, 3, 1, 2,
            lineHits: new Dictionary<int, int> { [1] = 5, [2] = 3, [3] = 0 },
            branchesByLine: new Dictionary<int, (int Covered, int Total)> { [2] = (1, 2) });

        Assert.Equal(2.0 / 3.0, f.LineRate!.Value, 4);
        Assert.Equal(1.0 / 3.0, f.StrictLineRate!.Value, 4);
        Assert.Equal(1, f.StrictlyHitLines);
        Assert.Equal(1, f.PartiallyHitLines);
    }

    [Fact]
    public void StrictLineRate_AllLinesPartial_IsZero()
    {
        // Boundary: every tracked line has unfinished branches → StrictLineRate must be 0,
        // even though LineRate stays at 1.0 (every line was executed).
        var f = Reports.ClassifiedFile("a.cs", 2, 2, 2, 4,
            lineHits: new Dictionary<int, int> { [10] = 1, [20] = 1 },
            branchesByLine: new Dictionary<int, (int Covered, int Total)>
            {
                [10] = (1, 2),
                [20] = (1, 2)
            });

        Assert.Equal(1.0, f.LineRate);
        Assert.Equal(0.0, f.StrictLineRate);
        Assert.Equal(0, f.StrictlyHitLines);
        Assert.Equal(2, f.PartiallyHitLines);
    }

    [Fact]
    public void StrictLineRate_EmptyReport_ReturnsNull()
    {
        Assert.Null(CoverageReport.Empty.StrictLineRate);
    }

    [Fact]
    public void FileCoverage_StrictLineRate_EmptyLines_ReturnsNull()
    {
        // Empty FileCoverage (no LineHits at all) — null, not a vacuous 1.0. Callers do have to
        // handle empty files, and the type now makes them: that is the point, not an inconvenience.
        var f = new FileCoverage("a.cs", 0, 0, 0, 0);

        Assert.Null(f.StrictLineRate);
    }

    [Fact]
    public void CoverageReport_StrictLineRate_SumsAcrossFiles()
    {
        // File A: 1 strict-hit + 1 partial + 1 miss → 1/3 strict.
        // File B: 2 strict-hits → 2/2 strict.
        // Combined: 3 strict-hits across 5 total lines → 60%.
        var report = new CoverageReport([
            Reports.ClassifiedFile("a.cs", 2, 3, 1, 2,
                lineHits: new Dictionary<int, int> { [1] = 1, [2] = 1, [3] = 0 },
                branchesByLine: new Dictionary<int, (int Covered, int Total)> { [2] = (1, 2) }),
            Reports.ClassifiedFile("b.cs", 2, 2, 0, 0,
                lineHits: new Dictionary<int, int> { [10] = 1, [20] = 1 },
                branchesByLine: new Dictionary<int, (int Covered, int Total)>())
        ]);

        Assert.Equal(0.6, report.StrictLineRate!.Value, 4);
    }

    [Fact]
    public void CoverageDiffResult_Improvements_FilterPositiveDeltas()
    {
        var diff = CoverageDiff.Compare(
            new CoverageReport([
                new FileCoverage("up.cs", 5, 10, 0, 0),
                new FileCoverage("down.cs", 9, 10, 0, 0)
            ]),
            new CoverageReport([
                new FileCoverage("up.cs", 9, 10, 0, 0),
                new FileCoverage("down.cs", 5, 10, 0, 0)
            ]));

        Assert.Single(diff.Improvements);
        Assert.Single(diff.Regressions);
        Assert.Equal("up.cs", diff.Improvements.Single().Path);
    }

    // ── Single-pass classification: precomputed counts vs. previous getter logic ──

    [Fact]
    public void Classify_MixedHitPartialFullMissed_CountsStrictAndPartial() =>
        AssertClassification(
            lineHits: new() { [1] = 5, [2] = 3, [3] = 7, [4] = 0 },
            branchesByLine: new() { [2] = (1, 2), [3] = (4, 4) },
            expectedStrict: 2, expectedPartial: 1);

    [Fact]
    public void Classify_AllHitNoBranches_AllStrict() =>
        AssertClassification(
            lineHits: new() { [1] = 1, [2] = 1, [3] = 1 },
            branchesByLine: new(),
            expectedStrict: 3, expectedPartial: 0);

    [Fact]
    public void Classify_EveryLinePartial_NoneStrict() =>
        AssertClassification(
            lineHits: new() { [10] = 1, [20] = 1 },
            branchesByLine: new() { [10] = (1, 2), [20] = (0, 2) },
            expectedStrict: 0, expectedPartial: 2);

    [Fact]
    public void Classify_AllMissed_NoneStrictOrPartial() =>
        AssertClassification(
            lineHits: new() { [1] = 0, [2] = 0 },
            branchesByLine: new(),
            expectedStrict: 0, expectedPartial: 0);

    [Fact]
    public void Classify_EmptyDict_NoneStrictOrPartial() =>
        AssertClassification(
            lineHits: new(),
            branchesByLine: new(),
            expectedStrict: 0, expectedPartial: 0);

    [Fact]
    public void Classify_BranchFullyCovered_CountsStrict() =>
        AssertClassification(
            lineHits: new() { [1] = 1 },
            branchesByLine: new() { [1] = (2, 2) },
            expectedStrict: 1, expectedPartial: 0);

    [Fact]
    public void Classify_BranchEntryOnMissedLine_StaysMiss() =>
        AssertClassification(
            lineHits: new() { [1] = 0 },
            branchesByLine: new() { [1] = (0, 2) },
            expectedStrict: 0, expectedPartial: 0);

    private static void AssertClassification(
        Dictionary<int, int> lineHits,
        Dictionary<int, (int Covered, int Total)> branchesByLine,
        int expectedStrict, int expectedPartial)
    {
        var f = Reports.ClassifiedFile("x.cs", 0, lineHits.Count, 0, 0,
            lineHits: lineHits, branchesByLine: branchesByLine);

        Assert.Equal(expectedStrict, f.StrictlyHitLines);
        Assert.Equal(expectedPartial, f.PartiallyHitLines);

        var strictViaStatus = lineHits.Keys.Count(k => f.GetLineStatus(k) is LineStatus.Hit);
        var partialViaStatus = lineHits.Keys.Count(k => f.GetLineStatus(k) is LineStatus.Partial);
        Assert.Equal(strictViaStatus, f.StrictlyHitLines);
        Assert.Equal(partialViaStatus, f.PartiallyHitLines);
    }

    [Fact]
    public void MergeWith_PrecomputedCountsMatchClassifyLines()
    {
        // MergeWith must compute the counts once over the merged dicts. The output struct's
        // StrictlyHitLines/PartiallyHitLines must agree with what a fresh classification
        // pass over the merged LineHits + BranchesByLine would produce — otherwise the
        // cached counts have drifted from the underlying data.
        var a = Reports.ClassifiedFile("a.cs", 1, 2, 1, 2,
            lineHits: new Dictionary<int, int> { [1] = 3, [2] = 0 },
            branchesByLine: new Dictionary<int, (int Covered, int Total)> { [1] = (1, 2) });
        var b = Reports.ClassifiedFile("a.cs", 2, 3, 2, 4,
            lineHits: new Dictionary<int, int> { [2] = 5, [3] = 1, [4] = 1 },
            branchesByLine: new Dictionary<int, (int Covered, int Total)> { [3] = (2, 2), [4] = (1, 2) });

        var (merged, _) = a.MergeWith(b);

        // Independently classify the merged dicts and compare. This is the "did MergeWith do
        // the same work the factory would have done?" check.
        var reference = Reports.ClassifiedFile(merged.Path,
            merged.LinesHit, merged.LinesTotal, merged.BranchesHit, merged.BranchesTotal,
            lineHits: merged.LineHits, branchesByLine: merged.BranchesByLine);

        Assert.Equal(reference.StrictlyHitLines, merged.StrictlyHitLines);
        Assert.Equal(reference.PartiallyHitLines, merged.PartiallyHitLines);

        // Concrete sanity numbers: lines 1 (partial after merge), 2 (strict — hits=5, no branch
        // for that line), 3 (strict — branch 2/2), 4 (partial — branch 1/2).
        // Line 2 had a branch entry in `a` but it was for a different line; line 2 itself never
        // had branch data, so post-merge it's strict.
        Assert.Equal(2, merged.StrictlyHitLines);
        Assert.Equal(2, merged.PartiallyHitLines);
    }

    // ── Coverage warnings: BranchTotalMismatch from cross-report MergeWith ──

    [Fact]
    public void Warnings_DefaultEmptyOnFreshReport()
    {
        // The init-only contract: a freshly-constructed CoverageReport has an empty Warnings
        // collection, not a null. Lets consumers safely iterate without null-checking.
        var report = new CoverageReport([new FileCoverage("a.cs", 1, 1, 0, 0)]);

        Assert.Empty(report.Warnings);
    }

    [Fact]
    public void Empty_HasEmptyWarnings()
    {
        Assert.Empty(CoverageReport.Empty.Warnings);
    }


    [Fact]
    public void MergeWith_BranchTotalMismatch_EmitsWarningAndKeepsMax()
    {
        // Two CI jobs disagree on the branch Total for line 10 — usually Release vs. Debug
        // builds. The merge keeps the larger total (Math.Max) and surfaces the divergence
        // as a structured warning so it stops being a silent bug.
        var a = new FileCoverage("src/Calculator.cs", 1, 1, 3, 5)
        {
            LineHits = new Dictionary<int, int> { [10] = 1 },
            BranchesByLine = new Dictionary<int, (int Covered, int Total)> { [10] = (3, 5) }
        };
        var b = new FileCoverage("src/Calculator.cs", 1, 1, 4, 7)
        {
            LineHits = new Dictionary<int, int> { [10] = 1 },
            BranchesByLine = new Dictionary<int, (int Covered, int Total)> { [10] = (4, 7) }
        };

        var (merged, warnings) = a.MergeWith(b);

        Assert.Equal((4, 7), merged.BranchesByLine[10]);
        var w = Assert.Single(warnings);
        Assert.Equal(CoverageWarningKind.BranchTotalMismatch, w.Kind);
        Assert.Equal("src/Calculator.cs", w.File);
        Assert.Equal(10, w.Line);
        Assert.Contains("Total 5 vs 7", w.Detail);
        Assert.Contains("keeping 7", w.Detail);
    }

    [Fact]
    public void MergeWith_MatchingTotals_EmitsNoWarning()
    {
        // Identical totals are a normal multi-upload — Math.Max on Covered, no divergence
        // to flag. Guards against false-positive noise on perfectly-aligned CI runs.
        var a = new FileCoverage("a.cs", 1, 1, 1, 2)
        {
            LineHits = new Dictionary<int, int> { [10] = 1 },
            BranchesByLine = new Dictionary<int, (int Covered, int Total)> { [10] = (1, 2) }
        };
        var b = new FileCoverage("a.cs", 1, 1, 2, 2)
        {
            LineHits = new Dictionary<int, int> { [10] = 1 },
            BranchesByLine = new Dictionary<int, (int Covered, int Total)> { [10] = (2, 2) }
        };

        var (merged, warnings) = a.MergeWith(b);

        Assert.Empty(warnings);
        Assert.Equal((2, 2), merged.BranchesByLine[10]);
    }

    [Fact]
    public void MergeWith_DisjointBranchLines_EmitsNoWarning()
    {
        // Different lines branched on each side — nothing to compare, nothing to warn about.
        var a = new FileCoverage("a.cs", 1, 1, 1, 2)
        {
            LineHits = new Dictionary<int, int> { [10] = 1 },
            BranchesByLine = new Dictionary<int, (int Covered, int Total)> { [10] = (1, 2) }
        };
        var b = new FileCoverage("a.cs", 1, 1, 1, 2)
        {
            LineHits = new Dictionary<int, int> { [20] = 1 },
            BranchesByLine = new Dictionary<int, (int Covered, int Total)> { [20] = (1, 2) }
        };

        var (_, warnings) = a.MergeWith(b);

        Assert.Empty(warnings);
    }

    [Fact]
    public void CoverageReport_Merge_PropagatesPerSideWarningsAndAppendsNew()
    {
        // a.Warnings + b.Warnings must carry through, and any new BranchTotalMismatch
        // surfaced by the per-file MergeWithWarnings call gets appended on top.
        var aFile = new FileCoverage("a.cs", 1, 1, 3, 5)
        {
            LineHits = new Dictionary<int, int> { [10] = 1 },
            BranchesByLine = new Dictionary<int, (int Covered, int Total)> { [10] = (3, 5) }
        };
        var bFile = new FileCoverage("a.cs", 1, 1, 4, 7)
        {
            LineHits = new Dictionary<int, int> { [10] = 1 },
            BranchesByLine = new Dictionary<int, (int Covered, int Total)> { [10] = (4, 7) }
        };

        var a = new CoverageReport([aFile])
        {
            Warnings =
            [
                new CoverageWarning(CoverageWarningKind.MalformedConditionCoverage, "x.cs", 1, "from-a")
            ]
        };
        var b = new CoverageReport([bFile])
        {
            Warnings =
            [
                new CoverageWarning(CoverageWarningKind.MalformedConditionCoverage, "y.cs", 2, "from-b")
            ]
        };

        var merged = CoverageReport.Merge(a, b);

        Assert.Equal(3, merged.Warnings.Count);
        Assert.Equal("from-a", merged.Warnings[0].Detail);
        Assert.Equal("from-b", merged.Warnings[1].Detail);
        Assert.Equal(CoverageWarningKind.BranchTotalMismatch, merged.Warnings[2].Kind);
        Assert.Equal(10, merged.Warnings[2].Line);
    }

    [Fact]
    public void CoverageReport_Merge_DistinctFiles_PropagatesWarningsWithoutNewOnes()
    {
        // No path overlap → no per-file MergeWithWarnings call → only the carried-over
        // warnings come through. Guards against the merge fabricating false anomalies.
        var a = new CoverageReport([new FileCoverage("a.cs", 1, 1, 0, 0)])
        {
            Warnings = [new CoverageWarning(CoverageWarningKind.MalformedConditionCoverage, "a.cs", 5, "raw")]
        };
        var b = new CoverageReport([new FileCoverage("b.cs", 1, 1, 0, 0)]);

        var merged = CoverageReport.Merge(a, b);

        var w = Assert.Single(merged.Warnings);
        Assert.Equal("a.cs", w.File);
    }

    [Fact]
    public void Exclude_PreservesWarnings_EvenWhenSourceFileFilteredOut()
    {
        // Filtering files for display doesn't change the fact that the parser observed an
        // anomaly. Warnings should stay observable on the filtered report so downstream
        // gates can still react to malformed inputs.
        var report = new CoverageReport([new FileCoverage("src/obj/Generated.cs", 1, 1, 0, 0)])
        {
            Warnings =
            [
                new CoverageWarning(CoverageWarningKind.MalformedConditionCoverage,
                    "src/obj/Generated.cs", 7, "raw")
            ]
        };

        var filtered = report.Exclude(ExclusionRules.WellKnown);

        Assert.Empty(filtered.Files);
        Assert.Single(filtered.Warnings);
    }
}
