using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;
[TestClass]
public sealed class ExclusionAndReportTests
{
    [TestMethod]
    public void WellKnown_HasNoStructurallyDeadRules()
    {
        // No "d__" rule: rules match the path (the filename), but coverlet only ever puts d__ in
        // the *class name*, so such a rule could never fire. The behavioral effect of every live
        // pattern is protected by the Exclude_WellKnown_* theories below; mirroring the list
        // contents here would just restate the data.
        Assert.DoesNotContain("d__", ExclusionRules.WellKnown);
        // No bare "GlobalUsings" substring either: it would swallow real product code in any
        // directory whose name contains it (src/GlobalUsingsGenerator/…).
        Assert.DoesNotContain("GlobalUsings", ExclusionRules.WellKnown);
    }

    [TestMethod]
    public void Exclude_WithKeep_RestoresFilesMatchingKeepPattern()
    {
        // `Program.cs` is in WellKnown, but a CLI tool whose entire surface lives there
        // can opt back in with `--keep Program.cs`.
        var report = new CoverageReport([new FileCoverage("src/MyApp/Program.cs", 10, 12, 0, 0), new FileCoverage("src/MyApp/Service.cs", 8, 10, 0, 0), new FileCoverage("src/MyApp/obj/Generated.cs", 1, 1, 0, 0)]);
        var filtered = report.Exclude(ExclusionRules.WellKnown, keep: ["Program.cs"]);
        Assert.Contains(f => f.Path.EndsWith("Program.cs"), filtered.Files); // exempted
        Assert.Contains(f => f.Path.EndsWith("Service.cs"), filtered.Files); // never excluded
        Assert.DoesNotContain(f => f.Path.Contains("/obj/"), filtered.Files); // still excluded
    }

    [TestMethod]
    public void Exclude_KeepNotMatched_LeavesExclusionInPlace()
    {
        var report = new CoverageReport([new FileCoverage("src/MyApp/Program.cs", 10, 12, 0, 0)]);
        var filtered = report.Exclude(ExclusionRules.WellKnown, keep: ["Worker.cs"]);
        Assert.IsEmpty(filtered.Files);
    }

    [TestMethod]
    public void Exclude_EmptyPatterns_ReturnsSameInstance()
    {
        var report = Reports.Mixed;
        var filtered = report.Exclude([]);
        Assert.AreSame(report, filtered);
    }

    [TestMethod]
    [DataRow("src/MyApp/obj/Debug/file.cs")]
    [DataRow("src/MyApp/OBJ/Debug/file.cs")]
    [DataRow("src/MyApp/bin/Release/file.cs")]
    [DataRow("src/Foo.g.cs")]
    [DataRow("src/Form.Designer.cs")]
    [DataRow("src/MyApp/Migrations/20230101_Init.cs")]
    [DataRow("src/GlobalUsings.cs")]
    public void Exclude_WellKnown_RemovesMatchingFile(string path)
    {
        var report = new CoverageReport([new FileCoverage(path, 1, 1, 0, 0), new FileCoverage("KeepThis.cs", 1, 1, 0, 0)]);
        var filtered = report.Exclude(ExclusionRules.WellKnown);
        Assert.DoesNotContain(f => f.Path == path, filtered.Files);
    }

    [TestMethod]
    [DataRow("Source/file.cs")]
    [DataRow("src/MyService.cs")]
    // Real product code whose *directory name* contains "GlobalUsings": an unanchored
    // substring rule would drop this whole directory, which could flip a failing
    // `check --min-line` gate to PASS.
    [DataRow("src/GlobalUsingsGenerator/Emitter.cs")]
    public void Exclude_WellKnown_KeepsNonMatchingFile(string path)
    {
        var report = new CoverageReport([new FileCoverage(path, 1, 1, 0, 0), new FileCoverage("KeepThis.cs", 1, 1, 0, 0)]);
        var filtered = report.Exclude(ExclusionRules.WellKnown);
        Assert.Contains(f => f.Path == path, filtered.Files);
    }

    [TestMethod]
    [DataRow("Program.cs")] // no directory prefix at all (project-root file)
    [DataRow("GlobalUsings.cs")]
    public void Exclude_WellKnown_LeadingSeparatorRules_MatchRootLevelPaths(string path)
    {
        // Emitters disagree on whether `filename` carries a directory prefix: the same
        // Program.cs must be excluded whether it arrives as "src/App/Program.cs" or as a
        // bare root-level "Program.cs". Matching runs against a virtually-rooted path so
        // "/Program.cs" anchors at the top level too.
        var report = new CoverageReport([new FileCoverage(path, 1, 1, 0, 0), new FileCoverage("KeepThis.cs", 1, 1, 0, 0)]);
        var filtered = report.Exclude(ExclusionRules.WellKnown);
        Assert.DoesNotContain(f => f.Path == path, filtered.Files);
        Assert.Contains(f => f.Path == "KeepThis.cs", filtered.Files);
    }

    [TestMethod]
    public void Exclude_WellKnown_CannotFlipAFailingGateByDroppingProductCode()
    {
        // The end-to-end hazard the anchored GlobalUsings rule prevents: 0%-covered product
        // code under src/GlobalUsingsGenerator/ must keep counting, so --exclude-generated
        // cannot turn a 20% report into a 100% PASS.
        var report = new CoverageReport([new FileCoverage("src/App/Covered.cs", 10, 10, 0, 0), new FileCoverage("src/GlobalUsingsGenerator/Emitter.cs", 0, 40, 0, 0)]);
        var gate = report.Exclude(ExclusionRules.WellKnown).Evaluate(80);
        Assert.AreEqual(GateOutcome.Fail, gate.Outcome);
    }

    [TestMethod]
    public void Exclude_DoesNotMutateSource()
    {
        var report = Reports.Mixed;
        var originalCount = report.Files.Count;
        _ = report.Exclude(["Unused"]);
        Assert.AreEqual(originalCount, report.Files.Count);
    }

    [TestMethod]
    public void Empty_StaticInstance_HasNoFilesAndNoRates()
    {
        // An empty report has no rates: claiming 1.0/1.0 would render a glob that matched
        // nothing as flawless coverage that clears every threshold.
        Assert.IsEmpty(CoverageReport.Empty.Files);
        Assert.IsNull(CoverageReport.Empty.LineRate);
        Assert.IsNull(CoverageReport.Empty.BranchRate);
        Assert.IsFalse(CoverageReport.Empty.HasLineData);
        Assert.IsFalse(CoverageReport.Empty.HasBranchData);
    }

    [TestMethod]
    public void HasBranchData_FileWithBranches_True()
    {
        var file = new FileCoverage("a.cs", 1, 2, 1, 2);
        Assert.IsTrue(file.HasBranchData);
    }

    [TestMethod]
    public void HasBranchData_FileWithoutBranches_False()
    {
        var file = new FileCoverage("a.cs", 1, 2, 0, 0);
        Assert.IsFalse(file.HasBranchData);
    }

    [TestMethod]
    public void HasBranchData_ReportWithoutAnyBranches_False()
    {
        Assert.IsFalse(Reports.LinesOnly.HasBranchData);
        Assert.IsTrue(Reports.Mixed.HasBranchData);
    }

    [TestMethod]
    public void FileCoverage_MergeWith_UnionsLinesAndAppendsPartialBranches()
    {
        // `a` covers line 5, misses 10, has a partial branch on line 15 (1/2).
        // `b` covers lines 20 and 30, misses 25, has a partial branch on line 25 (0/2).
        // Merging keeps the highest hit count per line and derives PartialBranches from the
        // unioned BranchesByLine dict (disjoint partial-branch lines → both surface).
        var a = Reports.ClassifiedFile("a.cs", linesHit: 1, linesTotal: 2, branchesHit: 1, branchesTotal: 2, lineHits: new Dictionary<int, int> { [5] = 3, [10] = 0 }, branchesByLine: new Dictionary<int, (int Covered, int Total)> { [15] = (1, 2) });
        var b = Reports.ClassifiedFile("a.cs", linesHit: 2, linesTotal: 3, branchesHit: 0, branchesTotal: 2, lineHits: new Dictionary<int, int> { [20] = 1, [25] = 0, [30] = 1 }, branchesByLine: new Dictionary<int, (int Covered, int Total)> { [25] = (0, 2) });
        var(merged, _) = a.MergeWith(b);
        Assert.AreSequenceEqual([10, 25], merged.UncoveredLines, SequenceOrder.InOrder);
        Assert.AreEqual(2, merged.PartialBranches.Count);
        Assert.AreEqual(3, merged.LinesHit); // 5, 20, 30
        Assert.AreEqual(5, merged.LinesTotal); // 5, 10, 20, 25, 30
        Assert.AreEqual(1, merged.BranchesHit); // 1 (line 15) + 0 (line 25)
        Assert.AreEqual(4, merged.BranchesTotal); // 2 + 2
    }

    [TestMethod]
    public void FileCoverage_MergeWith_BranchesOnlyInOther_PreservesEntry()
    {
        // `a` has the same line tracked but no branch data for it; `b` carries the branches.
        // The merge must keep `b`'s BranchesByLine entry: overwriting it with the empty dict
        // would silently flip the merged line from Partial back to Hit.
        var a = Reports.ClassifiedFile("a.cs", 1, 1, 0, 0, lineHits: new Dictionary<int, int> { [10] = 1 }, branchesByLine: new Dictionary<int, (int Covered, int Total)>());
        var b = Reports.ClassifiedFile("a.cs", 1, 1, 1, 2, lineHits: new Dictionary<int, int> { [10] = 1 }, branchesByLine: new Dictionary<int, (int Covered, int Total)> { [10] = (1, 2) });
        var(merged, _) = a.MergeWith(b);
        Assert.AreEqual((1, 2), merged.BranchesByLine[10]);
        Assert.AreEqual(LineStatus.Partial, merged.GetLineStatus(10));
    }

    // ── Codecov-style strict line classification (Hit / Partial / Miss) ──
    [TestMethod]
    public void GetLineStatus_FullyCoveredLineWithoutBranches_IsHit()
    {
        var f = Reports.ClassifiedFile("a.cs", 1, 1, 0, 0, lineHits: new Dictionary<int, int> { [10] = 3 }, branchesByLine: new Dictionary<int, (int Covered, int Total)>());
        Assert.AreEqual(LineStatus.Hit, f.GetLineStatus(10));
    }

    [TestMethod]
    public void GetLineStatus_FullyCoveredLineWithAllBranchesExercised_IsHit()
    {
        var f = Reports.ClassifiedFile("a.cs", 1, 1, 2, 2, lineHits: new Dictionary<int, int> { [10] = 3 }, branchesByLine: new Dictionary<int, (int Covered, int Total)> { [10] = (2, 2) });
        Assert.AreEqual(LineStatus.Hit, f.GetLineStatus(10));
    }

    [TestMethod]
    public void GetLineStatus_ExecutedLineWithIncompleteBranches_IsPartial()
    {
        var f = Reports.ClassifiedFile("a.cs", 1, 1, 1, 2, lineHits: new Dictionary<int, int> { [10] = 3 }, branchesByLine: new Dictionary<int, (int Covered, int Total)> { [10] = (1, 2) });
        Assert.AreEqual(LineStatus.Partial, f.GetLineStatus(10));
    }

    [TestMethod]
    public void GetLineStatus_ZeroHits_IsMissEvenWithBranchData()
    {
        var f = Reports.ClassifiedFile("a.cs", 0, 1, 0, 2, lineHits: new Dictionary<int, int> { [10] = 0 }, branchesByLine: new Dictionary<int, (int Covered, int Total)> { [10] = (0, 2) });
        Assert.AreEqual(LineStatus.Miss, f.GetLineStatus(10));
    }

    [TestMethod]
    public void GetLineStatus_UnknownLine_IsMiss()
    {
        var f = new FileCoverage("a.cs", 0, 0, 0, 0);
        Assert.AreEqual(LineStatus.Miss, f.GetLineStatus(999));
    }

    [TestMethod]
    public void TryGetLineStatus_TrackedHitLine_ReturnsTrueAndHit()
    {
        var f = new FileCoverage("a.cs", 1, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int>
            {
                [10] = 3
            }
        };
        Assert.IsTrue(f.TryGetLineStatus(10, out var status));
        Assert.AreEqual(LineStatus.Hit, status);
    }

    [TestMethod]
    public void TryGetLineStatus_TrackedZeroHitLine_ReturnsTrueAndMiss()
    {
        var f = new FileCoverage("a.cs", 0, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int>
            {
                [10] = 0
            }
        };
        Assert.IsTrue(f.TryGetLineStatus(10, out var status));
        Assert.AreEqual(LineStatus.Miss, status);
    }

    [TestMethod]
    public void TryGetLineStatus_TrackedPartialBranchLine_ReturnsTrueAndPartial()
    {
        var f = new FileCoverage("a.cs", 1, 1, 1, 2)
        {
            LineHits = new Dictionary<int, int>
            {
                [10] = 3
            },
            BranchesByLine = new Dictionary<int, (int Covered, int Total)>
            {
                [10] = (1, 2)
            }
        };
        Assert.IsTrue(f.TryGetLineStatus(10, out var status));
        Assert.AreEqual(LineStatus.Partial, status);
    }

    [TestMethod]
    public void TryGetLineStatus_UnknownLine_ReturnsFalseAndMiss()
    {
        // False signals "not tracked"; the out param is Miss for ergonomics.
        var f = new FileCoverage("a.cs", 0, 0, 0, 0);
        Assert.IsFalse(f.TryGetLineStatus(999, out var status));
        Assert.AreEqual(LineStatus.Miss, status);
    }

    [TestMethod]
    public void StrictLineRate_DowngradesPartialBranches()
    {
        // 3 lines tracked: line 1 fully hit (no branches), line 2 hit but with partial branches,
        // line 3 missed. Standard LineRate = 2/3 ≈ 66.7% (lines 1 and 2 are "hit").
        // Strict LineRate = 1/3 ≈ 33.3% (only line 1 is fully Hit; line 2 is Partial, line 3 is Miss).
        var f = Reports.ClassifiedFile("a.cs", 2, 3, 1, 2, lineHits: new Dictionary<int, int> { [1] = 5, [2] = 3, [3] = 0 }, branchesByLine: new Dictionary<int, (int Covered, int Total)> { [2] = (1, 2) });
        Assert.AreEqual(2.0 / 3.0, f.LineRate!.Value);
        Assert.AreEqual(1.0 / 3.0, f.StrictLineRate!.Value);
        Assert.AreEqual(1, f.StrictlyHitLines);
        Assert.AreEqual(1, f.PartiallyHitLines);
    }

    [TestMethod]
    public void StrictLineRate_AllLinesPartial_IsZero()
    {
        // Boundary: every tracked line has unfinished branches → StrictLineRate must be 0,
        // even though LineRate stays at 1.0 (every line was executed).
        var f = Reports.ClassifiedFile("a.cs", 2, 2, 2, 4, lineHits: new Dictionary<int, int> { [10] = 1, [20] = 1 }, branchesByLine: new Dictionary<int, (int Covered, int Total)> { [10] = (1, 2), [20] = (1, 2) });
        Assert.AreEqual(1.0, f.LineRate);
        Assert.AreEqual(0.0, f.StrictLineRate);
        Assert.AreEqual(0, f.StrictlyHitLines);
        Assert.AreEqual(2, f.PartiallyHitLines);
    }

    [TestMethod]
    public void StrictLineRate_EmptyReport_ReturnsNull()
    {
        Assert.IsNull(CoverageReport.Empty.StrictLineRate);
    }

    [TestMethod]
    public void FileCoverage_StrictLineRate_EmptyLines_ReturnsNull()
    {
        // Empty FileCoverage (no LineHits at all) — null, not a vacuous 1.0, so the type makes
        // callers handle empty files.
        var f = new FileCoverage("a.cs", 0, 0, 0, 0);
        Assert.IsNull(f.StrictLineRate);
    }

    [TestMethod]
    public void CoverageReport_StrictLineRate_SumsAcrossFiles()
    {
        // File A: 1 strict-hit + 1 partial + 1 miss → 1/3 strict.
        // File B: 2 strict-hits → 2/2 strict.
        // Combined: 3 strict-hits across 5 total lines → 60%.
        var report = new CoverageReport([Reports.ClassifiedFile("a.cs", 2, 3, 1, 2, lineHits: new Dictionary<int, int> { [1] = 1, [2] = 1, [3] = 0 }, branchesByLine: new Dictionary<int, (int Covered, int Total)> { [2] = (1, 2) }), Reports.ClassifiedFile("b.cs", 2, 2, 0, 0, lineHits: new Dictionary<int, int> { [10] = 1, [20] = 1 }, branchesByLine: new Dictionary<int, (int Covered, int Total)>())]);
        Assert.AreEqual(0.6, report.StrictLineRate!.Value);
    }

    [TestMethod]
    public void CoverageDiffResult_Improvements_FilterPositiveDeltas()
    {
        var diff = CoverageDiff.Compare(new CoverageReport([new FileCoverage("up.cs", 5, 10, 0, 0), new FileCoverage("down.cs", 9, 10, 0, 0)]), new CoverageReport([new FileCoverage("up.cs", 9, 10, 0, 0), new FileCoverage("down.cs", 5, 10, 0, 0)]));
        Assert.ContainsSingle(diff.Improvements);
        Assert.ContainsSingle(diff.Regressions);
        Assert.AreEqual("up.cs", diff.Improvements.Single().Path);
    }

    // ── Single-pass classification: precomputed counts agree with GetLineStatus ──
    [TestMethod]
    public void Classify_MixedHitPartialFullMissed_CountsStrictAndPartial() => AssertClassification(lineHits: new() { [1] = 5, [2] = 3, [3] = 7, [4] = 0 }, branchesByLine: new() { [2] = (1, 2), [3] = (4, 4) }, expectedStrict: 2, expectedPartial: 1);
    [TestMethod]
    public void Classify_AllHitNoBranches_AllStrict() => AssertClassification(lineHits: new() { [1] = 1, [2] = 1, [3] = 1 }, branchesByLine: new(), expectedStrict: 3, expectedPartial: 0);
    [TestMethod]
    public void Classify_EveryLinePartial_NoneStrict() => AssertClassification(lineHits: new() { [10] = 1, [20] = 1 }, branchesByLine: new() { [10] = (1, 2), [20] = (0, 2) }, expectedStrict: 0, expectedPartial: 2);
    [TestMethod]
    public void Classify_AllMissed_NoneStrictOrPartial() => AssertClassification(lineHits: new() { [1] = 0, [2] = 0 }, branchesByLine: new(), expectedStrict: 0, expectedPartial: 0);
    [TestMethod]
    public void Classify_EmptyDict_NoneStrictOrPartial() => AssertClassification(lineHits: new(), branchesByLine: new(), expectedStrict: 0, expectedPartial: 0);
    [TestMethod]
    public void Classify_BranchFullyCovered_CountsStrict() => AssertClassification(lineHits: new() { [1] = 1 }, branchesByLine: new() { [1] = (2, 2) }, expectedStrict: 1, expectedPartial: 0);
    [TestMethod]
    public void Classify_BranchEntryOnMissedLine_StaysMiss() => AssertClassification(lineHits: new() { [1] = 0 }, branchesByLine: new() { [1] = (0, 2) }, expectedStrict: 0, expectedPartial: 0);
    private static void AssertClassification(Dictionary<int, int> lineHits, Dictionary<int, (int Covered, int Total)> branchesByLine, int expectedStrict, int expectedPartial)
    {
        var f = Reports.ClassifiedFile("x.cs", 0, lineHits.Count, 0, 0, lineHits: lineHits, branchesByLine: branchesByLine);
        Assert.AreEqual(expectedStrict, f.StrictlyHitLines);
        Assert.AreEqual(expectedPartial, f.PartiallyHitLines);
        var strictViaStatus = lineHits.Keys.Count(k => f.GetLineStatus(k) is LineStatus.Hit);
        var partialViaStatus = lineHits.Keys.Count(k => f.GetLineStatus(k) is LineStatus.Partial);
        Assert.AreEqual(strictViaStatus, f.StrictlyHitLines);
        Assert.AreEqual(partialViaStatus, f.PartiallyHitLines);
    }

    [TestMethod]
    public void MergeWith_PrecomputedCountsMatchClassifyLines()
    {
        // MergeWith must compute the counts once over the merged dicts. The output struct's
        // StrictlyHitLines/PartiallyHitLines must agree with what a fresh classification
        // pass over the merged LineHits + BranchesByLine would produce — otherwise the
        // cached counts have drifted from the underlying data.
        var a = Reports.ClassifiedFile("a.cs", 1, 2, 1, 2, lineHits: new Dictionary<int, int> { [1] = 3, [2] = 0 }, branchesByLine: new Dictionary<int, (int Covered, int Total)> { [1] = (1, 2) });
        var b = Reports.ClassifiedFile("a.cs", 2, 3, 2, 4, lineHits: new Dictionary<int, int> { [2] = 5, [3] = 1, [4] = 1 }, branchesByLine: new Dictionary<int, (int Covered, int Total)> { [3] = (2, 2), [4] = (1, 2) });
        var(merged, _) = a.MergeWith(b);
        var reference = Reports.ClassifiedFile(merged.Path, merged.LinesHit, merged.LinesTotal, merged.BranchesHit, merged.BranchesTotal, lineHits: merged.LineHits, branchesByLine: merged.BranchesByLine);
        Assert.AreEqual(reference.StrictlyHitLines, merged.StrictlyHitLines);
        Assert.AreEqual(reference.PartiallyHitLines, merged.PartiallyHitLines);
        // Concrete sanity numbers: lines 1 (partial after merge), 2 (strict — hits=5, no branch
        // for that line), 3 (strict — branch 2/2), 4 (partial — branch 1/2).
        // Line 2 had a branch entry in `a` but it was for a different line; line 2 itself never
        // had branch data, so post-merge it's strict.
        Assert.AreEqual(2, merged.StrictlyHitLines);
        Assert.AreEqual(2, merged.PartiallyHitLines);
    }

    // ── Coverage warnings: BranchTotalMismatch from cross-report MergeWith ──
    [TestMethod]
    public void Warnings_DefaultEmptyOnFreshReport()
    {
        // The init-only contract: a freshly-constructed CoverageReport has an empty Warnings
        // collection, not a null. Lets consumers safely iterate without null-checking.
        var report = new CoverageReport([new FileCoverage("a.cs", 1, 1, 0, 0)]);
        Assert.IsEmpty(report.Warnings);
    }

    [TestMethod]
    public void Empty_HasEmptyWarnings()
    {
        Assert.IsEmpty(CoverageReport.Empty.Warnings);
    }

    [TestMethod]
    public void MergeWith_BranchTotalMismatch_EmitsWarningAndKeepsMax()
    {
        // Two CI jobs disagree on the branch Total for line 10 — usually Release vs. Debug
        // builds. The merge keeps the larger total (Math.Max) and surfaces the divergence
        // as a structured warning instead of hiding it.
        var a = new FileCoverage("src/Calculator.cs", 1, 1, 3, 5)
        {
            LineHits = new Dictionary<int, int>
            {
                [10] = 1
            },
            BranchesByLine = new Dictionary<int, (int Covered, int Total)>
            {
                [10] = (3, 5)
            }
        };
        var b = new FileCoverage("src/Calculator.cs", 1, 1, 4, 7)
        {
            LineHits = new Dictionary<int, int>
            {
                [10] = 1
            },
            BranchesByLine = new Dictionary<int, (int Covered, int Total)>
            {
                [10] = (4, 7)
            }
        };
        var(merged, warnings) = a.MergeWith(b);
        Assert.AreEqual((4, 7), merged.BranchesByLine[10]);
        var w = Assert.ContainsSingle(warnings);
        Assert.AreEqual(CoverageWarningKind.BranchTotalMismatch, w.Kind);
        Assert.AreEqual("src/Calculator.cs", w.File);
        Assert.AreEqual(10, w.Line);
        Assert.Contains("Total 5 vs 7", w.Detail);
        Assert.Contains("keeping 7", w.Detail);
    }

    [TestMethod]
    public void MergeWith_MatchingTotals_EmitsNoWarning()
    {
        // Identical totals are a normal multi-upload — Math.Max on Covered, no divergence
        // to flag. Guards against false-positive noise on perfectly-aligned CI runs.
        var a = new FileCoverage("a.cs", 1, 1, 1, 2)
        {
            LineHits = new Dictionary<int, int>
            {
                [10] = 1
            },
            BranchesByLine = new Dictionary<int, (int Covered, int Total)>
            {
                [10] = (1, 2)
            }
        };
        var b = new FileCoverage("a.cs", 1, 1, 2, 2)
        {
            LineHits = new Dictionary<int, int>
            {
                [10] = 1
            },
            BranchesByLine = new Dictionary<int, (int Covered, int Total)>
            {
                [10] = (2, 2)
            }
        };
        var(merged, warnings) = a.MergeWith(b);
        Assert.IsEmpty(warnings);
        Assert.AreEqual((2, 2), merged.BranchesByLine[10]);
    }

    [TestMethod]
    public void MergeWith_DisjointBranchLines_EmitsNoWarning()
    {
        // Different lines branched on each side — nothing to compare, nothing to warn about.
        var a = new FileCoverage("a.cs", 1, 1, 1, 2)
        {
            LineHits = new Dictionary<int, int>
            {
                [10] = 1
            },
            BranchesByLine = new Dictionary<int, (int Covered, int Total)>
            {
                [10] = (1, 2)
            }
        };
        var b = new FileCoverage("a.cs", 1, 1, 1, 2)
        {
            LineHits = new Dictionary<int, int>
            {
                [20] = 1
            },
            BranchesByLine = new Dictionary<int, (int Covered, int Total)>
            {
                [20] = (1, 2)
            }
        };
        var(_, warnings) = a.MergeWith(b);
        Assert.IsEmpty(warnings);
    }

    [TestMethod]
    public void CoverageReport_Merge_PropagatesPerSideWarningsAndAppendsNew()
    {
        // a.Warnings + b.Warnings must carry through, and any new BranchTotalMismatch
        // surfaced by the per-file MergeWith call gets appended on top.
        var aFile = new FileCoverage("a.cs", 1, 1, 3, 5)
        {
            LineHits = new Dictionary<int, int>
            {
                [10] = 1
            },
            BranchesByLine = new Dictionary<int, (int Covered, int Total)>
            {
                [10] = (3, 5)
            }
        };
        var bFile = new FileCoverage("a.cs", 1, 1, 4, 7)
        {
            LineHits = new Dictionary<int, int>
            {
                [10] = 1
            },
            BranchesByLine = new Dictionary<int, (int Covered, int Total)>
            {
                [10] = (4, 7)
            }
        };
        var a = new CoverageReport([aFile])
        {
            Warnings = [new CoverageWarning(CoverageWarningKind.MalformedConditionCoverage, "x.cs", 1, "from-a")]
        };
        var b = new CoverageReport([bFile])
        {
            Warnings = [new CoverageWarning(CoverageWarningKind.MalformedConditionCoverage, "y.cs", 2, "from-b")]
        };
        var merged = CoverageReport.Merge(a, b);
        Assert.AreEqual(3, merged.Warnings.Count);
        Assert.AreEqual("from-a", merged.Warnings[0].Detail);
        Assert.AreEqual("from-b", merged.Warnings[1].Detail);
        Assert.AreEqual(CoverageWarningKind.BranchTotalMismatch, merged.Warnings[2].Kind);
        Assert.AreEqual(10, merged.Warnings[2].Line);
    }

    [TestMethod]
    public void CoverageReport_Merge_DistinctFiles_PropagatesWarningsWithoutNewOnes()
    {
        // No path overlap → no per-file MergeWith call → only the carried-over
        // warnings come through. Guards against the merge fabricating false anomalies.
        var a = new CoverageReport([new FileCoverage("a.cs", 1, 1, 0, 0)])
        {
            Warnings = [new CoverageWarning(CoverageWarningKind.MalformedConditionCoverage, "a.cs", 5, "raw")]
        };
        var b = new CoverageReport([new FileCoverage("b.cs", 1, 1, 0, 0)]);
        var merged = CoverageReport.Merge(a, b);
        var w = Assert.ContainsSingle(merged.Warnings);
        Assert.AreEqual("a.cs", w.File);
    }

    [TestMethod]
    public void Exclude_PreservesWarnings_EvenWhenSourceFileFilteredOut()
    {
        // Filtering files for display doesn't change the fact that the parser observed an
        // anomaly. Warnings should stay observable on the filtered report so downstream
        // gates can still react to malformed inputs.
        var report = new CoverageReport([new FileCoverage("src/obj/Generated.cs", 1, 1, 0, 0)])
        {
            Warnings = [new CoverageWarning(CoverageWarningKind.MalformedConditionCoverage, "src/obj/Generated.cs", 7, "raw")]
        };
        var filtered = report.Exclude(ExclusionRules.WellKnown);
        Assert.IsEmpty(filtered.Files);
        Assert.ContainsSingle(filtered.Warnings);
    }
}
