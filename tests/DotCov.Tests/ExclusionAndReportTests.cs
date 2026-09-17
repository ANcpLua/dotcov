using TUnit.Assertions.Enums;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

public sealed class ExclusionAndReportTests
{
    [Test]
    public async Task WellKnown_HasNoStructurallyDeadRules()
    {
        // No "d__" rule: it matched on Path (the filename), but coverlet only ever puts d__ in the
        // *class name* — so the rule was structurally dead. Assert it's gone, not present.
        // (The behavioral effect of every live pattern is protected by the Exclude_WellKnown_*
        // theories below — mirroring the list contents here would just restate the data.)
        await Assert.That(ExclusionRules.WellKnown).DoesNotContain("d__");
        // No bare "GlobalUsings" substring either: it swallowed real product code in any
        // directory whose name contains it (src/GlobalUsingsGenerator/…).
        await Assert.That(ExclusionRules.WellKnown).DoesNotContain("GlobalUsings");
    }

    [Test]
    public async Task Exclude_WithKeep_RestoresFilesMatchingKeepPattern()
    {
        // `Program.cs` is in WellKnown, but a CLI tool whose entire surface lives there
        // can opt back in with `--keep Program.cs`.
        var report = new CoverageReport([
            new FileCoverage("src/MyApp/Program.cs", 10, 12, 0, 0),
            new FileCoverage("src/MyApp/Service.cs", 8, 10, 0, 0),
            new FileCoverage("src/MyApp/obj/Generated.cs", 1, 1, 0, 0)
        ]);

        var filtered = report.Exclude(ExclusionRules.WellKnown, keep: ["Program.cs"]);

        await Assert.That(filtered.Files).Contains(f => f.Path.EndsWith("Program.cs"));    // exempted
        await Assert.That(filtered.Files).Contains(f => f.Path.EndsWith("Service.cs"));    // never excluded
        await Assert.That(filtered.Files).DoesNotContain(f => f.Path.Contains("/obj/"));   // still excluded
    }

    [Test]
    public async Task Exclude_KeepNotMatched_LeavesExclusionInPlace()
    {
        var report = new CoverageReport([
            new FileCoverage("src/MyApp/Program.cs", 10, 12, 0, 0)
        ]);

        var filtered = report.Exclude(ExclusionRules.WellKnown, keep: ["Worker.cs"]);

        await Assert.That(filtered.Files).IsEmpty();
    }

    [Test]
    public async Task Exclude_EmptyPatterns_ReturnsSameInstance()
    {
        var report = Reports.Mixed;

        var filtered = report.Exclude([]);

        await Assert.That(filtered).IsSameReferenceAs(report);
    }

    [Test]
    [Arguments("src/MyApp/obj/Debug/file.cs")]
    [Arguments("src/MyApp/OBJ/Debug/file.cs")]
    [Arguments("src/MyApp/bin/Release/file.cs")]
    [Arguments("src/Foo.g.cs")]
    [Arguments("src/Form.Designer.cs")]
    [Arguments("src/MyApp/Migrations/20230101_Init.cs")]
    [Arguments("src/GlobalUsings.cs")]
    public async Task Exclude_WellKnown_RemovesMatchingFile(string path)
    {
        var report = new CoverageReport([
            new FileCoverage(path, 1, 1, 0, 0),
            new FileCoverage("KeepThis.cs", 1, 1, 0, 0)
        ]);

        var filtered = report.Exclude(ExclusionRules.WellKnown);

        await Assert.That(filtered.Files).DoesNotContain(f => f.Path == path);
    }

    [Test]
    [Arguments("Source/file.cs")]
    [Arguments("src/MyService.cs")]
    // Real product code whose *directory name* contains "GlobalUsings" — the old unanchored
    // substring rule silently dropped this whole directory, which could flip a failing
    // `check --min-line` gate to PASS.
    [Arguments("src/GlobalUsingsGenerator/Emitter.cs")]
    public async Task Exclude_WellKnown_KeepsNonMatchingFile(string path)
    {
        var report = new CoverageReport([
            new FileCoverage(path, 1, 1, 0, 0),
            new FileCoverage("KeepThis.cs", 1, 1, 0, 0)
        ]);

        var filtered = report.Exclude(ExclusionRules.WellKnown);

        await Assert.That(filtered.Files).Contains(f => f.Path == path);
    }

    [Test]
    [Arguments("Program.cs")]        // no directory prefix at all (project-root file)
    [Arguments("GlobalUsings.cs")]
    public async Task Exclude_WellKnown_LeadingSeparatorRules_MatchRootLevelPaths(string path)
    {
        // Emitters disagree on whether `filename` carries a directory prefix: the same
        // Program.cs must be excluded whether it arrives as "src/App/Program.cs" or as a
        // bare root-level "Program.cs". Matching runs against a virtually-rooted path so
        // "/Program.cs" anchors at the top level too.
        var report = new CoverageReport([
            new FileCoverage(path, 1, 1, 0, 0),
            new FileCoverage("KeepThis.cs", 1, 1, 0, 0)
        ]);

        var filtered = report.Exclude(ExclusionRules.WellKnown);

        await Assert.That(filtered.Files).DoesNotContain(f => f.Path == path);
        await Assert.That(filtered.Files).Contains(f => f.Path == "KeepThis.cs");
    }

    [Test]
    public async Task Exclude_WellKnown_CannotFlipAFailingGateByDroppingProductCode()
    {
        // The end-to-end hazard the anchored GlobalUsings rule prevents: 0%-covered product
        // code under src/GlobalUsingsGenerator/ must keep counting, so --exclude-generated
        // cannot turn a 20% report into a 100% PASS.
        var report = new CoverageReport([
            new FileCoverage("src/App/Covered.cs", 10, 10, 0, 0),
            new FileCoverage("src/GlobalUsingsGenerator/Emitter.cs", 0, 40, 0, 0)
        ]);

        var gate = report.Exclude(ExclusionRules.WellKnown).Evaluate(80);

        await Assert.That(gate.Outcome).IsEqualTo(GateOutcome.Fail);
    }

    [Test]
    public async Task Exclude_DoesNotMutateSource()
    {
        var report = Reports.Mixed;
        var originalCount = report.Files.Count;

        _ = report.Exclude(["Unused"]);

        await Assert.That(report.Files.Count).IsEqualTo(originalCount);
    }

    [Test]
    public async Task Empty_StaticInstance_HasNoFilesAndNoRates()
    {
        // The empty report is the whole point of this change: it used to claim 1.0/1.0, so a
        // glob that matched nothing rendered as flawless coverage and cleared every threshold.
        await Assert.That(CoverageReport.Empty.Files).IsEmpty();
        await Assert.That(CoverageReport.Empty.LineRate).IsNull();
        await Assert.That(CoverageReport.Empty.BranchRate).IsNull();
        await Assert.That(CoverageReport.Empty.HasLineData).IsFalse();
        await Assert.That(CoverageReport.Empty.HasBranchData).IsFalse();
    }

    [Test]
    public async Task HasBranchData_FileWithBranches_True()
    {
        var file = new FileCoverage("a.cs", 1, 2, 1, 2);
        await Assert.That(file.HasBranchData).IsTrue();
    }

    [Test]
    public async Task HasBranchData_FileWithoutBranches_False()
    {
        var file = new FileCoverage("a.cs", 1, 2, 0, 0);
        await Assert.That(file.HasBranchData).IsFalse();
    }

    [Test]
    public async Task HasBranchData_ReportWithoutAnyBranches_False()
    {
        await Assert.That(Reports.LinesOnly.HasBranchData).IsFalse();
        await Assert.That(Reports.Mixed.HasBranchData).IsTrue();
    }

    [Test]
    public async Task FileCoverage_MergeWith_UnionsLinesAndAppendsPartialBranches()
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

        await Assert.That(merged.UncoveredLines).IsEquivalentTo([10, 25], CollectionOrdering.Matching);
        await Assert.That(merged.PartialBranches.Count).IsEqualTo(2);
        await Assert.That(merged.LinesHit).IsEqualTo(3);      // 5, 20, 30
        await Assert.That(merged.LinesTotal).IsEqualTo(5);    // 5, 10, 20, 25, 30
        await Assert.That(merged.BranchesHit).IsEqualTo(1);   // 1 (line 15) + 0 (line 25)
        await Assert.That(merged.BranchesTotal).IsEqualTo(4); // 2 + 2
    }

    [Test]
    public async Task FileCoverage_MergeWith_BranchesOnlyInOther_PreservesEntry()
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

        await Assert.That(merged.BranchesByLine[10]).IsEqualTo((1, 2));
        await Assert.That(merged.GetLineStatus(10)).IsEqualTo(LineStatus.Partial);
    }

    // ── Codecov-style strict line classification (Hit / Partial / Miss) ──

    [Test]
    public async Task GetLineStatus_FullyCoveredLineWithoutBranches_IsHit()
    {
        var f = Reports.ClassifiedFile("a.cs", 1, 1, 0, 0,
            lineHits: new Dictionary<int, int> { [10] = 3 },
            branchesByLine: new Dictionary<int, (int Covered, int Total)>());

        await Assert.That(f.GetLineStatus(10)).IsEqualTo(LineStatus.Hit);
    }

    [Test]
    public async Task GetLineStatus_FullyCoveredLineWithAllBranchesExercised_IsHit()
    {
        var f = Reports.ClassifiedFile("a.cs", 1, 1, 2, 2,
            lineHits: new Dictionary<int, int> { [10] = 3 },
            branchesByLine: new Dictionary<int, (int Covered, int Total)> { [10] = (2, 2) });

        await Assert.That(f.GetLineStatus(10)).IsEqualTo(LineStatus.Hit);
    }

    [Test]
    public async Task GetLineStatus_ExecutedLineWithIncompleteBranches_IsPartial()
    {
        var f = Reports.ClassifiedFile("a.cs", 1, 1, 1, 2,
            lineHits: new Dictionary<int, int> { [10] = 3 },
            branchesByLine: new Dictionary<int, (int Covered, int Total)> { [10] = (1, 2) });

        await Assert.That(f.GetLineStatus(10)).IsEqualTo(LineStatus.Partial);
    }

    [Test]
    public async Task GetLineStatus_ZeroHits_IsMissEvenWithBranchData()
    {
        var f = Reports.ClassifiedFile("a.cs", 0, 1, 0, 2,
            lineHits: new Dictionary<int, int> { [10] = 0 },
            branchesByLine: new Dictionary<int, (int Covered, int Total)> { [10] = (0, 2) });

        await Assert.That(f.GetLineStatus(10)).IsEqualTo(LineStatus.Miss);
    }

    [Test]
    public async Task GetLineStatus_UnknownLine_IsMiss()
    {
        var f = new FileCoverage("a.cs", 0, 0, 0, 0);

        await Assert.That(f.GetLineStatus(999)).IsEqualTo(LineStatus.Miss);
    }

    [Test]
    public async Task TryGetLineStatus_TrackedHitLine_ReturnsTrueAndHit()
    {
        var f = new FileCoverage("a.cs", 1, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 3 }
        };

        await Assert.That(f.TryGetLineStatus(10, out var status)).IsTrue();
        await Assert.That(status).IsEqualTo(LineStatus.Hit);
    }

    [Test]
    public async Task TryGetLineStatus_TrackedZeroHitLine_ReturnsTrueAndMiss()
    {
        // The key contract: line IS tracked but had zero hits. Distinct from untracked.
        var f = new FileCoverage("a.cs", 0, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 0 }
        };

        await Assert.That(f.TryGetLineStatus(10, out var status)).IsTrue();
        await Assert.That(status).IsEqualTo(LineStatus.Miss);
    }

    [Test]
    public async Task TryGetLineStatus_TrackedPartialBranchLine_ReturnsTrueAndPartial()
    {
        var f = new FileCoverage("a.cs", 1, 1, 1, 2)
        {
            LineHits = new Dictionary<int, int> { [10] = 3 },
            BranchesByLine = new Dictionary<int, (int Covered, int Total)> { [10] = (1, 2) }
        };

        await Assert.That(f.TryGetLineStatus(10, out var status)).IsTrue();
        await Assert.That(status).IsEqualTo(LineStatus.Partial);
    }

    [Test]
    public async Task TryGetLineStatus_UnknownLine_ReturnsFalseAndMiss()
    {
        // The other key contract: false signals "not tracked", out param is Miss for ergonomics.
        var f = new FileCoverage("a.cs", 0, 0, 0, 0);

        await Assert.That(f.TryGetLineStatus(999, out var status)).IsFalse();
        await Assert.That(status).IsEqualTo(LineStatus.Miss);
    }

    [Test]
    public async Task StrictLineRate_DowngradesPartialBranches()
    {
        // 3 lines tracked: line 1 fully hit (no branches), line 2 hit but with partial branches,
        // line 3 missed. Standard LineRate = 2/3 ≈ 66.7% (lines 1 and 2 are "hit").
        // Strict LineRate = 1/3 ≈ 33.3% (only line 1 is fully Hit; line 2 is Partial, line 3 is Miss).
        var f = Reports.ClassifiedFile("a.cs", 2, 3, 1, 2,
            lineHits: new Dictionary<int, int> { [1] = 5, [2] = 3, [3] = 0 },
            branchesByLine: new Dictionary<int, (int Covered, int Total)> { [2] = (1, 2) });

        await Assert.That(f.LineRate!.Value).IsEqualTo(2.0 / 3.0);
        await Assert.That(f.StrictLineRate!.Value).IsEqualTo(1.0 / 3.0);
        await Assert.That(f.StrictlyHitLines).IsEqualTo(1);
        await Assert.That(f.PartiallyHitLines).IsEqualTo(1);
    }

    [Test]
    public async Task StrictLineRate_AllLinesPartial_IsZero()
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

        await Assert.That(f.LineRate).IsEqualTo(1.0);
        await Assert.That(f.StrictLineRate).IsEqualTo(0.0);
        await Assert.That(f.StrictlyHitLines).IsEqualTo(0);
        await Assert.That(f.PartiallyHitLines).IsEqualTo(2);
    }

    [Test]
    public async Task StrictLineRate_EmptyReport_ReturnsNull()
    {
        await Assert.That(CoverageReport.Empty.StrictLineRate).IsNull();
    }

    [Test]
    public async Task FileCoverage_StrictLineRate_EmptyLines_ReturnsNull()
    {
        // Empty FileCoverage (no LineHits at all) — null, not a vacuous 1.0. Callers do have to
        // handle empty files, and the type now makes them: that is the point, not an inconvenience.
        var f = new FileCoverage("a.cs", 0, 0, 0, 0);

        await Assert.That(f.StrictLineRate).IsNull();
    }

    [Test]
    public async Task CoverageReport_StrictLineRate_SumsAcrossFiles()
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

        await Assert.That(report.StrictLineRate!.Value).IsEqualTo(0.6);
    }

    [Test]
    public async Task CoverageDiffResult_Improvements_FilterPositiveDeltas()
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

        await Assert.That(diff.Improvements).HasSingleItem();
        await Assert.That(diff.Regressions).HasSingleItem();
        await Assert.That(diff.Improvements.Single().Path).IsEqualTo("up.cs");
    }

    // ── Single-pass classification: precomputed counts vs. previous getter logic ──

    [Test]
    public async Task Classify_MixedHitPartialFullMissed_CountsStrictAndPartial() =>
        await AssertClassification(
            lineHits: new() { [1] = 5, [2] = 3, [3] = 7, [4] = 0 },
            branchesByLine: new() { [2] = (1, 2), [3] = (4, 4) },
            expectedStrict: 2, expectedPartial: 1);

    [Test]
    public async Task Classify_AllHitNoBranches_AllStrict() =>
        await AssertClassification(
            lineHits: new() { [1] = 1, [2] = 1, [3] = 1 },
            branchesByLine: new(),
            expectedStrict: 3, expectedPartial: 0);

    [Test]
    public async Task Classify_EveryLinePartial_NoneStrict() =>
        await AssertClassification(
            lineHits: new() { [10] = 1, [20] = 1 },
            branchesByLine: new() { [10] = (1, 2), [20] = (0, 2) },
            expectedStrict: 0, expectedPartial: 2);

    [Test]
    public async Task Classify_AllMissed_NoneStrictOrPartial() =>
        await AssertClassification(
            lineHits: new() { [1] = 0, [2] = 0 },
            branchesByLine: new(),
            expectedStrict: 0, expectedPartial: 0);

    [Test]
    public async Task Classify_EmptyDict_NoneStrictOrPartial() =>
        await AssertClassification(
            lineHits: new(),
            branchesByLine: new(),
            expectedStrict: 0, expectedPartial: 0);

    [Test]
    public async Task Classify_BranchFullyCovered_CountsStrict() =>
        await AssertClassification(
            lineHits: new() { [1] = 1 },
            branchesByLine: new() { [1] = (2, 2) },
            expectedStrict: 1, expectedPartial: 0);

    [Test]
    public async Task Classify_BranchEntryOnMissedLine_StaysMiss() =>
        await AssertClassification(
            lineHits: new() { [1] = 0 },
            branchesByLine: new() { [1] = (0, 2) },
            expectedStrict: 0, expectedPartial: 0);

    private static async Task AssertClassification(
        Dictionary<int, int> lineHits,
        Dictionary<int, (int Covered, int Total)> branchesByLine,
        int expectedStrict, int expectedPartial)
    {
        var f = Reports.ClassifiedFile("x.cs", 0, lineHits.Count, 0, 0,
            lineHits: lineHits, branchesByLine: branchesByLine);

        await Assert.That(f.StrictlyHitLines).IsEqualTo(expectedStrict);
        await Assert.That(f.PartiallyHitLines).IsEqualTo(expectedPartial);

        var strictViaStatus = lineHits.Keys.Count(k => f.GetLineStatus(k) is LineStatus.Hit);
        var partialViaStatus = lineHits.Keys.Count(k => f.GetLineStatus(k) is LineStatus.Partial);
        await Assert.That(f.StrictlyHitLines).IsEqualTo(strictViaStatus);
        await Assert.That(f.PartiallyHitLines).IsEqualTo(partialViaStatus);
    }

    [Test]
    public async Task MergeWith_PrecomputedCountsMatchClassifyLines()
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

        await Assert.That(merged.StrictlyHitLines).IsEqualTo(reference.StrictlyHitLines);
        await Assert.That(merged.PartiallyHitLines).IsEqualTo(reference.PartiallyHitLines);

        // Concrete sanity numbers: lines 1 (partial after merge), 2 (strict — hits=5, no branch
        // for that line), 3 (strict — branch 2/2), 4 (partial — branch 1/2).
        // Line 2 had a branch entry in `a` but it was for a different line; line 2 itself never
        // had branch data, so post-merge it's strict.
        await Assert.That(merged.StrictlyHitLines).IsEqualTo(2);
        await Assert.That(merged.PartiallyHitLines).IsEqualTo(2);
    }

    // ── Coverage warnings: BranchTotalMismatch from cross-report MergeWith ──

    [Test]
    public async Task Warnings_DefaultEmptyOnFreshReport()
    {
        // The init-only contract: a freshly-constructed CoverageReport has an empty Warnings
        // collection, not a null. Lets consumers safely iterate without null-checking.
        var report = new CoverageReport([new FileCoverage("a.cs", 1, 1, 0, 0)]);

        await Assert.That(report.Warnings).IsEmpty();
    }

    [Test]
    public async Task Empty_HasEmptyWarnings()
    {
        await Assert.That(CoverageReport.Empty.Warnings).IsEmpty();
    }


    [Test]
    public async Task MergeWith_BranchTotalMismatch_EmitsWarningAndKeepsMax()
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

        await Assert.That(merged.BranchesByLine[10]).IsEqualTo((4, 7));
        var w = await Assert.That(warnings).HasSingleItem();
        await Assert.That(w.Kind).IsEqualTo(CoverageWarningKind.BranchTotalMismatch);
        await Assert.That(w.File).IsEqualTo("src/Calculator.cs");
        await Assert.That(w.Line).IsEqualTo(10);
        await Assert.That(w.Detail).Contains("Total 5 vs 7");
        await Assert.That(w.Detail).Contains("keeping 7");
    }

    [Test]
    public async Task MergeWith_MatchingTotals_EmitsNoWarning()
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

        await Assert.That(warnings).IsEmpty();
        await Assert.That(merged.BranchesByLine[10]).IsEqualTo((2, 2));
    }

    [Test]
    public async Task MergeWith_DisjointBranchLines_EmitsNoWarning()
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

        await Assert.That(warnings).IsEmpty();
    }

    [Test]
    public async Task CoverageReport_Merge_PropagatesPerSideWarningsAndAppendsNew()
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

        await Assert.That(merged.Warnings.Count).IsEqualTo(3);
        await Assert.That(merged.Warnings[0].Detail).IsEqualTo("from-a");
        await Assert.That(merged.Warnings[1].Detail).IsEqualTo("from-b");
        await Assert.That(merged.Warnings[2].Kind).IsEqualTo(CoverageWarningKind.BranchTotalMismatch);
        await Assert.That(merged.Warnings[2].Line).IsEqualTo(10);
    }

    [Test]
    public async Task CoverageReport_Merge_DistinctFiles_PropagatesWarningsWithoutNewOnes()
    {
        // No path overlap → no per-file MergeWithWarnings call → only the carried-over
        // warnings come through. Guards against the merge fabricating false anomalies.
        var a = new CoverageReport([new FileCoverage("a.cs", 1, 1, 0, 0)])
        {
            Warnings = [new CoverageWarning(CoverageWarningKind.MalformedConditionCoverage, "a.cs", 5, "raw")]
        };
        var b = new CoverageReport([new FileCoverage("b.cs", 1, 1, 0, 0)]);

        var merged = CoverageReport.Merge(a, b);

        var w = await Assert.That(merged.Warnings).HasSingleItem();
        await Assert.That(w.File).IsEqualTo("a.cs");
    }

    [Test]
    public async Task Exclude_PreservesWarnings_EvenWhenSourceFileFilteredOut()
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

        await Assert.That(filtered.Files).IsEmpty();
        await Assert.That(filtered.Warnings).HasSingleItem();
    }
}