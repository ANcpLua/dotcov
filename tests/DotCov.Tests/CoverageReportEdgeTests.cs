using DotCov.Tests.Infrastructure;
using TUnit.Assertions.Enums;

namespace DotCov.Tests;

/// <summary>
/// Edge cases of the report model reached through its public surface: condition detail on a
/// line without a line-level branch aggregate, the cross-convention scan when the two sides
/// share a path, root identity for drive-relative spellings, and the gate's boundaries.
/// </summary>
public sealed class CoverageReportEdgeTests
{
    [Test]
    public async Task MergeWith_ConditionDetailWithoutLineAggregate_PresentsTheDerivedAggregate()
    {
        // A hand-built record may carry ConditionsByLine without a BranchesByLine entry for
        // that line. The merge derives the line from its conditions: two 2-way jumps, one
        // covered on both outcomes and one on neither, union across the sides to 3 of 4.
        var a = new FileCoverage("src/A.cs", 1, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [7] = 1 },
            ConditionsByLine = new Dictionary<int, IReadOnlyDictionary<int, int>> { [7] = new Dictionary<int, int> { [0] = 2, [4] = 0 } }
        };
        var b = new FileCoverage("src/A.cs", 1, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [7] = 1 },
            ConditionsByLine = new Dictionary<int, IReadOnlyDictionary<int, int>> { [7] = new Dictionary<int, int> { [0] = 0, [4] = 1 } }
        };

        var (merged, warnings) = a.MergeWith(b);

        await Assert.That(warnings).IsEmpty();
        await Assert.That(merged.BranchesByLine[7]).IsEqualTo((3, 4));
        await Assert.That(merged.BranchesHit).IsEqualTo(3);
        await Assert.That(merged.BranchesTotal).IsEqualTo(4);
        await Assert.That(merged.PartialBranches).IsEquivalentTo([new BranchDetail(7, 3, 4)], CollectionOrdering.Matching);
        await Assert.That(merged.GetLineStatus(7)).IsEqualTo(LineStatus.Partial);
        await Assert.That(merged.PartiallyHitLines).IsEqualTo(1);
    }

    [Test]
    public async Task Merge_DifferentRoots_SharedPathIsMergedAndNeverFlaggedAmbiguous()
    {
        // The ambiguity scan only looks at files unique to each side: a path both sides carry
        // is the same key, merged by line, and must not be reported as a possible duplicate.
        var a = new CoverageReport([
            new FileCoverage("/repo/src/Shared.cs", 1, 2, 0, 0) { LineHits = new Dictionary<int, int> { [1] = 1, [2] = 0 } },
            new FileCoverage("/repo/src/Util.cs", 1, 1, 0, 0) { LineHits = new Dictionary<int, int> { [1] = 1 } }
        ]) { SourceRoots = ["/repo"] };
        var b = new CoverageReport([
            new FileCoverage("/repo/src/Shared.cs", 1, 2, 0, 0) { LineHits = new Dictionary<int, int> { [1] = 0, [2] = 1 } },
            new FileCoverage("/_/src/Util.cs", 0, 1, 0, 0) { LineHits = new Dictionary<int, int> { [1] = 0 } }
        ]) { SourceRoots = ["/_"] };

        var merged = CoverageReport.Merge(a, b);

        var shared = merged.Files.Single(f => f.Path == "/repo/src/Shared.cs");
        await Assert.That(shared.LinesHit).IsEqualTo(2);
        await Assert.That(shared.LinesTotal).IsEqualTo(2);
        var warning = await Assert.That(merged.Warnings).HasSingleItem();
        await Assert.That(warning.Kind).IsEqualTo(CoverageWarningKind.FileIdentityAmbiguous);
        await Assert.That(warning.File).IsEqualTo("/repo/src/Util.cs");
        await Assert.That(warning.Detail).Contains("'/_/src/Util.cs'");
        await Assert.That(merged.Warnings.Any(w => w.Detail.Contains("Shared.cs", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    [Arguments("c:repo", "C:repo", 2)]      // drive-relative: no separator after the colon, left as spelled
    [Arguments("cd:/repo", "Cd:/repo", 2)]  // not a drive letter: second character is not ':'
    [Arguments("c:/repo/", @"C:\repo", 1)]  // drive-absolute: one identity regardless of case and slashes
    public async Task Merge_RootIdentity_UppercasesOnlyAnAbsoluteDriveLetter(string left, string right, int expectedRoots)
    {
        var a = new CoverageReport([]) { SourceRoots = [left] };
        var b = new CoverageReport([]) { SourceRoots = [right] };

        var merged = CoverageReport.Merge(a, b);

        await Assert.That(merged.SourceRoots.Count).IsEqualTo(expectedRoots);
        await Assert.That(merged.SourceRoots[0]).IsEqualTo(left);
    }

    [Test]
    public async Task Parse_LowercaseDriveRelativeRoot_KeepsItsSpelling()
    {
        var report = Cobertura.NewDoc()
            .WithSource("c:repo")
            .AddClass("A.cs", c => c.Line(1, 1))
            .Parse();

        await Assert.That(report.SourceRoots).IsEquivalentTo(["c:repo"], CollectionOrdering.Matching);
        await Assert.That(report.Files.Single().Path).IsEqualTo("c:repo/A.cs");
    }

    // ── Count-only records: counts not backed by per-line data ────────────────

    [Test]
    public async Task Merge_CountOnlyRecordWithItself_KeepsItsCounts()
    {
        // The positional constructor builds a record from counts alone. Reconciling it with
        // itself must keep those counts, not rebuild them from the (empty) per-line data as 0/0.
        var report = Reports.Single("src/A.cs", hit: 5, total: 10, bHit: 3, bTotal: 4);

        var merged = CoverageReport.Merge(report, report);

        var file = await Assert.That(merged.Files).HasSingleItem();
        await Assert.That(file.LinesHit).IsEqualTo(5);
        await Assert.That(file.LinesTotal).IsEqualTo(10);
        await Assert.That(file.BranchesHit).IsEqualTo(3);
        await Assert.That(file.BranchesTotal).IsEqualTo(4);
        await Assert.That(merged.Evaluate(50, 75).Outcome).IsEqualTo(GateOutcome.Pass);
    }

    [Test]
    public async Task Merge_CountOnlyRecords_ReconcileEachCountWithMathMax()
    {
        var merged = CoverageReport.Merge(
            Reports.Single("src/A.cs", hit: 5, total: 10, bHit: 0, bTotal: 8),
            Reports.Single("src/A.cs", hit: 7, total: 9, bHit: 3, bTotal: 4));

        var file = merged.Files.Single();
        await Assert.That((file.LinesHit, file.LinesTotal)).IsEqualTo((7, 10));
        await Assert.That((file.BranchesHit, file.BranchesTotal)).IsEqualTo((3, 8));
    }

    [Test]
    public async Task Merge_LineDataWithCountOnlyRecord_NeverDropsTheUnbackedLines()
    {
        // 2 of 2 lines with per-line data plus a count-only 0 of 50 merge to 2/50: the 50 lines
        // the count-only side vouches for stay counted, so the gate fails at 80.
        var withLines = new CoverageReport([new FileCoverage("src/A.cs", 2, 2, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [1] = 1, [2] = 1 }
        }]);
        var countOnly = Reports.Single("src/A.cs", hit: 0, total: 50);

        var merged = CoverageReport.Merge(withLines, countOnly);

        var file = merged.Files.Single();
        await Assert.That((file.LinesHit, file.LinesTotal)).IsEqualTo((2, 50));
        await Assert.That(file.LineHits.Keys).IsEquivalentTo([1, 2], CollectionOrdering.Any);
        await Assert.That(merged.Evaluate(80).Outcome).IsEqualTo(GateOutcome.Fail);
    }

    [Test]
    public async Task Merge_CountsWithInconsistentLineData_AreNotBackedAndStillCount()
    {
        // Per-line keys that match the total but disagree on hits do not back the counts:
        // the record vouches for 5 hit lines, its per-line data for 1.
        var claimed = new CoverageReport([new FileCoverage("src/A.cs", 5, 10, 0, 0)
        {
            LineHits = Enumerable.Range(1, 10).ToDictionary(l => l, l => l == 1 ? 1 : 0)
        }]);

        var merged = CoverageReport.Merge(claimed, claimed);

        await Assert.That(merged.Files.Single().LinesHit).IsEqualTo(5);
        await Assert.That(merged.Files.Single().LinesTotal).IsEqualTo(10);
    }

    [Test]
    public async Task Merge_BranchCountsWithoutBranchData_SurviveAMergeWithLineData()
    {
        // Lines are backed on both sides, branches only counted on one: the line dimension
        // merges by line, the branch dimension falls back to Math.Max of the counts.
        var a = new CoverageReport([new FileCoverage("src/A.cs", 1, 2, 3, 4)
        {
            LineHits = new Dictionary<int, int> { [1] = 1, [2] = 0 }
        }]);
        var b = new CoverageReport([new FileCoverage("src/A.cs", 1, 2, 1, 2)
        {
            LineHits = new Dictionary<int, int> { [1] = 0, [2] = 1 },
            BranchesByLine = new Dictionary<int, (int Covered, int Total)> { [2] = (1, 2) }
        }]);

        var file = CoverageReport.Merge(a, b).Files.Single();

        await Assert.That((file.LinesHit, file.LinesTotal)).IsEqualTo((2, 2));
        await Assert.That((file.BranchesHit, file.BranchesTotal)).IsEqualTo((3, 4));
        await Assert.That(file.BranchesByLine[2]).IsEqualTo((1, 2));
    }

    [Test]
    [Arguments("PQC")]
    [Arguments("PCQ")]
    [Arguments("CPQ")]
    [Arguments("QCP")]
    public async Task Merge_CountOnlyFallback_IsIndependentOfFoldOrder(string order)
    {
        // P and Q carry per-line data for different lines, C only counts. A plain Math.Max of
        // the two sides' counts would give 4/50 for (P+Q)+C but 2/50 for (P+C)+Q.
        var reports = order.Select(name => name switch
        {
            'P' => new CoverageReport([new FileCoverage("src/A.cs", 2, 2, 0, 0) { LineHits = new Dictionary<int, int> { [1] = 1, [2] = 1 } }]),
            'Q' => new CoverageReport([new FileCoverage("src/A.cs", 2, 2, 0, 0) { LineHits = new Dictionary<int, int> { [3] = 1, [4] = 1 } }]),
            _ => Reports.Single("src/A.cs", hit: 0, total: 50)
        }).ToArray();

        var left = CoverageReport.Merge(CoverageReport.Merge(reports[0], reports[1]), reports[2]).Files.Single();
        var right = CoverageReport.Merge(reports[0], CoverageReport.Merge(reports[1], reports[2])).Files.Single();

        await Assert.That((left.LinesHit, left.LinesTotal)).IsEqualTo((4, 50));
        await Assert.That((right.LinesHit, right.LinesTotal)).IsEqualTo((4, 50));
    }

    [Test]
    public async Task Merge_ParserBuiltReports_AreUnchangedByTheCountFallback()
    {
        // Per-line data on both sides never takes the count fallback: union of lines,
        // per-condition union, and a poisoned line keeps only its raw aggregate, even though
        // one side's own count (2 covered, from its condition overlay) is higher.
        var a = Cobertura.NewDoc().AddClass("src/W.cs", c => c
                .Line(1, 3).Line(2, 0)
                .BranchWithConditions(5, "25% (1/4)", (1, "100%"), (2, "0%"))
                .Branch(9, "50% (1/2)"))
            .Parse();
        var b = Cobertura.NewDoc().AddClass("src/W.cs", c => c
                .Line(2, 1).Line(3, 0)
                .BranchWithConditions(5, "25% (1/4)", (1, "0%"), (3, "50%"))
                .Branch(9, "100% (2/2)"))
            .Parse();

        var merged = CoverageReport.Merge(a, b);

        var file = merged.Files.Single();
        await Assert.That(a.Files.Single().BranchesByLine[5]).IsEqualTo((2, 4));
        await Assert.That((file.LinesHit, file.LinesTotal)).IsEqualTo((4, 5));
        await Assert.That(file.LineHits.Keys).IsEquivalentTo([1, 2, 3, 5, 9], CollectionOrdering.Any);
        await Assert.That(file.BranchesByLine[5]).IsEqualTo((1, 4));
        await Assert.That(file.BranchesByLine[9]).IsEqualTo((2, 2));
        await Assert.That((file.BranchesHit, file.BranchesTotal)).IsEqualTo((3, 6));
        await Assert.That(file.UncoveredLines).IsEquivalentTo([3], CollectionOrdering.Matching);
        await Assert.That(merged.Warnings.Single().Kind).IsEqualTo(CoverageWarningKind.ConditionIdentityMismatch);
    }

    // ── Gate boundaries ───────────────────────────────────────────────────────

    [Test]
    public async Task Evaluate_BranchThresholdOnly_PassesOnBranchesWithoutJudgingLines()
    {
        var report = Reports.Single("src/A.cs", hit: 1, total: 10, bHit: 3, bTotal: 4);

        var gate = report.Evaluate(minLinePercent: 0, minBranchPercent: 75);

        await Assert.That(gate.Outcome).IsEqualTo(GateOutcome.Pass);
        await Assert.That(gate.LineBelowThreshold).IsFalse();
        await Assert.That(gate.ToString()).IsEqualTo("PASS: line 10.0% (min 0%), branch 75.0% (min 75%) - thresholds met");
    }

    [Test]
    public async Task Evaluate_BranchThresholdOnly_FailsOnBranchesAlone()
    {
        var report = Reports.Single("src/A.cs", hit: 10, total: 10, bHit: 2, bTotal: 4);

        var gate = report.Evaluate(minLinePercent: 0, minBranchPercent: 75);

        await Assert.That(gate.Outcome).IsEqualTo(GateOutcome.Fail);
        await Assert.That(gate.Reason).IsEqualTo("branch coverage below threshold");
        await Assert.That(gate.BranchBelowThreshold).IsTrue();
    }

    [Test]
    public async Task Evaluate_HundredPercentThreshold_FailsOneLineShortAndPrintsNoRoundedHundred()
    {
        // 19999/20000 = 99.995%: F1 rounding prints 100.0%; the failing dimension floors.
        var report = Reports.Single("src/A.cs", hit: 19999, total: 20000);

        var gate = report.Evaluate(100);

        await Assert.That(gate.Outcome).IsEqualTo(GateOutcome.Fail);
        await Assert.That(gate.ToString()).IsEqualTo("FAIL: line 99.9% (min 100%), branch n/a (min 0%) - line coverage below threshold");
        await Assert.That(report.BelowPercent(100).Single().Path).IsEqualTo("src/A.cs");
    }

    [Test]
    public async Task Evaluate_NaNBranchThresholdWithoutBranchData_FailsInsteadOfThrowing()
    {
        // NaN is neither "> 0" (so the branch NoData check does not fire) nor "<= 0" (so the
        // branch dimension is judged): with no branch data the verdict must still be a
        // non-pass, never an exception. Evaluate is public, so NaN reaches it even though the
        // CLI and Fallout threshold parsers reject it.
        var report = Reports.Single("src/A.cs", hit: 9, total: 10);

        var gate = report.Evaluate(80, double.NaN);

        await Assert.That(gate.Outcome).IsEqualTo(GateOutcome.Fail);
        await Assert.That(gate.IsPass).IsFalse();
        await Assert.That(gate.Reason).IsEqualTo("branch coverage below threshold");
    }

    [Test]
    public async Task Evaluate_FilesWithoutLineData_IsNoDataEvenWithBranchData()
    {
        // Branch counts without any line data cannot answer a line threshold.
        var report = Reports.Single("src/A.cs", hit: 0, total: 0, bHit: 4, bTotal: 4);

        var gate = report.Evaluate(minLinePercent: 50, minBranchPercent: 50);

        await Assert.That(gate.Outcome).IsEqualTo(GateOutcome.NoData);
        await Assert.That(gate.IsPass).IsFalse();
        await Assert.That(gate.LineRate).IsNull();
        await Assert.That(gate.BranchRate).IsNull();
    }
}
