using TUnit.Assertions.Enums;
using System.Text.Json;
using DotCov.Formatters;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

public sealed class JsonFormatterTests
{
    [Test]
    public async Task Format_ProducesValidJson()
    {
        var json = JsonFormatter.Format(Reports.Mixed);

        var parsed = JsonDocument.Parse(json);
        await Assert.That(parsed.RootElement.GetProperty("summary").ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert.That(parsed.RootElement.GetProperty("files").ValueKind).IsEqualTo(JsonValueKind.Array);
    }

    [Test]
    public async Task Format_Summary_RoundsRatesToTwoDecimals()
    {
        var json = JsonFormatter.Format(Reports.Mixed);
        var summary = JsonDocument.Parse(json).RootElement.GetProperty("summary");

        // 7/12 lines = 58.3333… → rounded to 58.33
        await Assert.That(summary.GetProperty("lineRate").GetDouble()).IsEqualTo(58.33);
    }

    [Test]
    public async Task Format_WithBranchData_SetsHasBranchDataTrue()
    {
        var json = JsonFormatter.Format(Reports.Mixed);
        var summary = JsonDocument.Parse(json).RootElement.GetProperty("summary");

        await Assert.That(summary.GetProperty("hasBranchData").GetBoolean()).IsTrue();
        await Assert.That(summary.GetProperty("branchRate").ValueKind).IsEqualTo(JsonValueKind.Number);
    }

    [Test]
    public async Task Format_NoBranchData_SetsHasBranchDataFalseAndOmitsBranchRate()
    {
        var json = JsonFormatter.Format(Reports.LinesOnly);
        var summary = JsonDocument.Parse(json).RootElement.GetProperty("summary");

        await Assert.That(summary.GetProperty("hasBranchData").GetBoolean()).IsFalse();
        // Null values are omitted (DefaultIgnoreCondition.WhenWritingNull) — that's the contract.
        await Assert.That(summary.TryGetProperty("branchRate", out _)).IsFalse();
    }

    [Test]
    public async Task Format_PerFileBranchRate_IsOmittedWhenNoBranches()
    {
        var json = JsonFormatter.Format(Reports.Mixed);
        var files = JsonDocument.Parse(json).RootElement.GetProperty("files");

        var unused = files.EnumerateArray().Single(f => f.GetProperty("path").GetString() == "src/Unused.cs");
        await Assert.That(unused.TryGetProperty("branchRate", out _)).IsFalse();
    }

    [Test]
    public void Format_OmitsUncoveredLines_WhenList_IsEmpty()
    {
        var report = new CoverageReport([
            new FileCoverage("a.cs", 1, 1, 0, 0) { UncoveredLines = [] }
        ]);

        var json = JsonFormatter.Format(report);
        var file = JsonDocument.Parse(json).RootElement.GetProperty("files")[0];

        Assert.ThrowsExactly<KeyNotFoundException>(() => file.GetProperty("uncoveredLines"));
    }

    [Test]
    public async Task Format_IncludesUncoveredLines_WhenPopulated()
    {
        var report = new CoverageReport([
            new FileCoverage("a.cs", 1, 3, 0, 0) { UncoveredLines = [10, 20, 30] }
        ]);

        var json = JsonFormatter.Format(report);
        var file = JsonDocument.Parse(json).RootElement.GetProperty("files")[0];
        var uncovered = file.GetProperty("uncoveredLines").EnumerateArray().Select(e => e.GetInt32()).ToArray();

        await Assert.That(uncovered).IsEquivalentTo([10, 20, 30], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Format_IncludesPartialBranches_WhenPopulated()
    {
        var report = new CoverageReport([
            new FileCoverage("a.cs", 1, 1, 1, 2) { PartialBranches = [new BranchDetail(15, 1, 2)] }
        ]);

        var json = JsonFormatter.Format(report);
        var file = JsonDocument.Parse(json).RootElement.GetProperty("files")[0];
        var partial = file.GetProperty("partialBranches")[0];

        await Assert.That(partial.GetProperty("line").GetInt32()).IsEqualTo(15);
        await Assert.That(partial.GetProperty("covered").GetInt32()).IsEqualTo(1);
        await Assert.That(partial.GetProperty("total").GetInt32()).IsEqualTo(2);
    }

    [Test]
    public async Task Format_Summary_CarriesBranchTotals()
    {
        // totalBranches/coveredBranches are part of the wire contract — asserted by value
        // so the writer statements cannot be deleted wholesale. WriteSummary is shared, so
        // this also pins the snapshot and diff summary paths.
        var json = JsonFormatter.Format(Reports.Mixed);
        var summary = JsonDocument.Parse(json).RootElement.GetProperty("summary");

        await Assert.That(summary.GetProperty("totalBranches").GetInt32()).IsEqualTo(6);
        await Assert.That(summary.GetProperty("coveredBranches").GetInt32()).IsEqualTo(3);
    }

    [Test]
    public async Task Format_PerFile_CarriesAllFourCountFields()
    {
        var json = JsonFormatter.Format(Reports.Mixed);
        var files = JsonDocument.Parse(json).RootElement.GetProperty("files");

        var parser = files.EnumerateArray().Single(f => f.GetProperty("path").GetString() == "src/Parser.cs");
        await Assert.That(parser.GetProperty("linesHit").GetInt32()).IsEqualTo(3);
        await Assert.That(parser.GetProperty("linesTotal").GetInt32()).IsEqualTo(5);
        await Assert.That(parser.GetProperty("branchesHit").GetInt32()).IsEqualTo(1);
        await Assert.That(parser.GetProperty("branchesTotal").GetInt32()).IsEqualTo(4);
    }

    [Test]
    public async Task Format_EmptyPartialBranches_OmitsKey()
    {
        // Absent-key-means-clean: an empty partialBranches list must omit the key entirely,
        // same contract as uncoveredLines/warnings/lineChanges.
        var report = new CoverageReport([
            new FileCoverage("a.cs", 1, 1, 2, 2) { PartialBranches = [] }
        ]);

        var json = JsonFormatter.Format(report);
        var file = JsonDocument.Parse(json).RootElement.GetProperty("files")[0];

        await Assert.That(file.TryGetProperty("partialBranches", out _)).IsFalse();
    }

    [Test]
    public async Task FormatDiff_ProducesValidJsonWithSummaryAndFiles()
    {
        var diff = CoverageDiff.Compare(
            new CoverageReport([new FileCoverage("a.cs", 5, 10, 0, 0)]),
            new CoverageReport([new FileCoverage("a.cs", 8, 10, 0, 0)]));

        var json = JsonFormatter.FormatDiff(diff);
        var root = JsonDocument.Parse(json).RootElement;

        await Assert.That(root.GetProperty("summary").GetProperty("before").GetDouble()).IsEqualTo(50.0);
        await Assert.That(root.GetProperty("summary").GetProperty("after").GetDouble()).IsEqualTo(80.0);
        await Assert.That(root.GetProperty("summary").GetProperty("delta").GetDouble()).IsEqualTo(30.0);
    }

    [Test]
    public async Task FormatDiff_NullBefore_IsOmittedFromOutput()
    {
        var diff = CoverageDiff.Compare(
            CoverageReport.Empty,
            new CoverageReport([new FileCoverage("new.cs", 5, 10, 0, 0)]));

        var json = JsonFormatter.FormatDiff(diff);
        var file = JsonDocument.Parse(json).RootElement.GetProperty("files")[0];

        // Same null-omission contract as the report serializer.
        await Assert.That(file.TryGetProperty("before", out _)).IsFalse();
        await Assert.That(file.GetProperty("change").GetString()).IsEqualTo("added");
    }

    [Test]
    public async Task FormatDiff_NullAfter_IsOmittedFromOutput()
    {
        var diff = CoverageDiff.Compare(
            new CoverageReport([new FileCoverage("gone.cs", 4, 5, 0, 0)]),
            CoverageReport.Empty);

        var json = JsonFormatter.FormatDiff(diff);
        var file = JsonDocument.Parse(json).RootElement.GetProperty("files")[0];

        await Assert.That(file.TryGetProperty("after", out _)).IsFalse();
        await Assert.That(file.GetProperty("before").GetDouble()).IsEqualTo(80.0);
        await Assert.That(file.GetProperty("change").GetString()).IsEqualTo("removed");
    }

    [Test]
    public async Task FormatDiff_IndirectLineChanges_AppearInJsonPayload()
    {
        var before = new CoverageReport([new FileCoverage("a.cs", 1, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 5 }
        }]);
        var after = new CoverageReport([new FileCoverage("a.cs", 0, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 0 }
        }]);

        var json = JsonFormatter.FormatDiff(CoverageDiff.Compare(before, after));
        var root = JsonDocument.Parse(json).RootElement;

        await Assert.That(root.GetProperty("summary").GetProperty("indirectLineChanges").GetInt32()).IsEqualTo(1);

        var lineChange = root.GetProperty("files")[0].GetProperty("lineChanges")[0];
        await Assert.That(lineChange.GetProperty("line").GetInt32()).IsEqualTo(10);
        await Assert.That(lineChange.GetProperty("change").GetString()).IsEqualTo("newlymissed");
        await Assert.That(lineChange.GetProperty("beforeHits").GetInt32()).IsEqualTo(5);
        await Assert.That(lineChange.GetProperty("afterHits").GetInt32()).IsEqualTo(0);
    }

    [Test]
    public async Task FormatDiff_AddedLine_EmitsAddedChangeWithOnlyAfterHits()
    {
        // Same file on both sides; line 30 only exists in After → LineDelta.Added variant.
        // Wire format: change="added", beforeHits omitted (null), afterHits populated.
        var before = new CoverageReport([new FileCoverage("a.cs", 1, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 1 }
        }]);
        var after = new CoverageReport([new FileCoverage("a.cs", 1, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 1, [30] = 7 }
        }]);

        var json = JsonFormatter.FormatDiff(CoverageDiff.Compare(before, after));
        var lineChange = JsonDocument.Parse(json).RootElement
            .GetProperty("files")[0].GetProperty("lineChanges")[0];

        await Assert.That(lineChange.GetProperty("line").GetInt32()).IsEqualTo(30);
        await Assert.That(lineChange.GetProperty("change").GetString()).IsEqualTo("added");
        await Assert.That(lineChange.GetProperty("afterHits").GetInt32()).IsEqualTo(7);
        await Assert.That(lineChange.TryGetProperty("beforeHits", out _)).IsFalse();
    }

    [Test]
    public async Task FormatDiff_RemovedLine_EmitsRemovedChangeWithOnlyBeforeHits()
    {
        // Line 20 dropped from After → LineDelta.Removed variant.
        // Wire format: change="removed", beforeHits populated, afterHits omitted (null).
        var before = new CoverageReport([new FileCoverage("a.cs", 1, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 1, [20] = 4 }
        }]);
        var after = new CoverageReport([new FileCoverage("a.cs", 1, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 1 }
        }]);

        var json = JsonFormatter.FormatDiff(CoverageDiff.Compare(before, after));
        var lineChange = JsonDocument.Parse(json).RootElement
            .GetProperty("files")[0].GetProperty("lineChanges")[0];

        await Assert.That(lineChange.GetProperty("line").GetInt32()).IsEqualTo(20);
        await Assert.That(lineChange.GetProperty("change").GetString()).IsEqualTo("removed");
        await Assert.That(lineChange.GetProperty("beforeHits").GetInt32()).IsEqualTo(4);
        await Assert.That(lineChange.TryGetProperty("afterHits", out _)).IsFalse();
    }

    [Test]
    public async Task FormatDiff_NewlyHitLine_EmitsNewlyHitChangeWithBothHits()
    {
        // Line 10 missed before, hit now → LineDelta.NewlyHit variant.
        // Wire format: change="newlyhit", both beforeHits (0) and afterHits populated.
        var before = new CoverageReport([new FileCoverage("a.cs", 0, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 0 }
        }]);
        var after = new CoverageReport([new FileCoverage("a.cs", 1, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 3 }
        }]);

        var json = JsonFormatter.FormatDiff(CoverageDiff.Compare(before, after));
        var lineChange = JsonDocument.Parse(json).RootElement
            .GetProperty("files")[0].GetProperty("lineChanges")[0];

        await Assert.That(lineChange.GetProperty("line").GetInt32()).IsEqualTo(10);
        await Assert.That(lineChange.GetProperty("change").GetString()).IsEqualTo("newlyhit");
        await Assert.That(lineChange.GetProperty("beforeHits").GetInt32()).IsEqualTo(0);
        await Assert.That(lineChange.GetProperty("afterHits").GetInt32()).IsEqualTo(3);
    }

    [Test]
    public async Task FormatDiff_NoIndirectChanges_LineChangesAbsent()
    {
        var diff = CoverageDiff.Compare(
            new CoverageReport([new FileCoverage("a.cs", 5, 10, 0, 0)]),
            new CoverageReport([new FileCoverage("a.cs", 8, 10, 0, 0)]));

        var json = JsonFormatter.FormatDiff(diff);
        var file = JsonDocument.Parse(json).RootElement.GetProperty("files")[0];

        await Assert.That(file.TryGetProperty("lineChanges", out _)).IsFalse();
    }

    [Test]
    public async Task FormatDiff_ModifiedFile_CarriesPathAndAfterByValue()
    {
        var diff = CoverageDiff.Compare(
            new CoverageReport([new FileCoverage("a.cs", 5, 10, 0, 0)]),
            new CoverageReport([new FileCoverage("a.cs", 8, 10, 0, 0)]));

        var json = JsonFormatter.FormatDiff(diff);
        var file = JsonDocument.Parse(json).RootElement.GetProperty("files")[0];

        // path and after asserted by value: existing tests index files positionally and read
        // "before" only, leaving both writer statements deletable.
        await Assert.That(file.GetProperty("path").GetString()).IsEqualTo("a.cs");
        await Assert.That(file.GetProperty("after").GetDouble()).IsEqualTo(80.0);
        await Assert.That(file.GetProperty("change").GetString()).IsEqualTo("modified");
    }

    [Test]
    public async Task FormatSnapshot_FilesArray_CarriesPerFileCounts()
    {
        // The snapshot's files loop must actually run: an empty "files" array satisfies a
        // ValueKind.Array check, so pin the length and the first file's counts by value.
        var snapshot = new CoverageSnapshot(
            CommitSha: "abc123", Branch: "main", Project: "App",
            Timestamp: DateTimeOffset.UnixEpoch, FileHash: null, Report: Reports.Mixed);

        var json = JsonFormatter.FormatSnapshot(snapshot);
        var files = JsonDocument.Parse(json).RootElement.GetProperty("files");

        await Assert.That(files.GetArrayLength()).IsEqualTo(3);
        var calc = files.EnumerateArray().Single(f => f.GetProperty("path").GetString() == "src/Calculator.cs");
        await Assert.That(calc.GetProperty("linesHit").GetInt32()).IsEqualTo(4);
        await Assert.That(calc.GetProperty("linesTotal").GetInt32()).IsEqualTo(4);
        await Assert.That(calc.GetProperty("branchesHit").GetInt32()).IsEqualTo(2);
        await Assert.That(calc.GetProperty("branchesTotal").GetInt32()).IsEqualTo(2);
    }

    [Test]
    public async Task FormatSnapshot_IncludesAllMetadata()
    {
        var snapshot = new CoverageSnapshot(
            CommitSha: "abc123",
            Branch: "main",
            Project: "MyApp",
            Timestamp: new DateTimeOffset(2026, 5, 11, 12, 0, 0, TimeSpan.Zero),
            FileHash: "deadbeef",
            Report: Reports.FullyCovered);

        var json = JsonFormatter.FormatSnapshot(snapshot);
        var root = JsonDocument.Parse(json).RootElement;

        await Assert.That(root.GetProperty("commit").GetString()).IsEqualTo("abc123");
        await Assert.That(root.GetProperty("branch").GetString()).IsEqualTo("main");
        await Assert.That(root.GetProperty("project").GetString()).IsEqualTo("MyApp");
        await Assert.That(root.GetProperty("fileHash").GetString()).IsEqualTo("deadbeef");
        await Assert.That(root.GetProperty("summary").ValueKind).IsEqualTo(JsonValueKind.Object);
        await Assert.That(root.GetProperty("files").ValueKind).IsEqualTo(JsonValueKind.Array);
    }

    [Test]
    public void FormatSnapshot_NullFileHash_IsOmitted()
    {
        var snapshot = new CoverageSnapshot(
            CommitSha: "x", Branch: "y", Project: "z",
            Timestamp: DateTimeOffset.UnixEpoch, FileHash: null,
            Report: CoverageReport.Empty);

        var json = JsonFormatter.FormatSnapshot(snapshot);
        var root = JsonDocument.Parse(json).RootElement;

        Assert.ThrowsExactly<KeyNotFoundException>(() => root.GetProperty("fileHash"));
    }

    [Test]
    public async Task Format_UsesCamelCasePropertyNames()
    {
        var json = JsonFormatter.Format(Reports.Mixed);

        await Assert.That(json).Contains("\"lineRate\"");
        await Assert.That(json).Contains("\"branchRate\"");
        await Assert.That(json).Contains("\"totalLines\"");
        await Assert.That(json).Contains("\"coveredLines\"");
    }

    [Test]
    public async Task FormatSnapshot_WithWarnings_IncludesWarningsArray()
    {
        // The snapshot is the payload built for remote sinks — the anomaly channel must
        // survive the pipeline boundary, with the same shape Format emits.
        var report = new CoverageReport([new FileCoverage("src/A.cs", 1, 1, 0, 0)])
        {
            Warnings = [new CoverageWarning(CoverageWarningKind.BranchTotalMismatch, "src/A.cs", 12, "Total 5 vs 7")]
        };
        var snapshot = new CoverageSnapshot(
            CommitSha: "abc123", Branch: "main", Project: "App",
            Timestamp: DateTimeOffset.UnixEpoch, FileHash: null, Report: report);

        var json = JsonFormatter.FormatSnapshot(snapshot);
        var warnings = JsonDocument.Parse(json).RootElement.GetProperty("warnings");

        await Assert.That(warnings.GetArrayLength()).IsEqualTo(1);
        await Assert.That(warnings[0].GetProperty("kind").GetString()).IsEqualTo("BranchTotalMismatch");
        await Assert.That(warnings[0].GetProperty("file").GetString()).IsEqualTo("src/A.cs");
        await Assert.That(warnings[0].GetProperty("line").GetInt32()).IsEqualTo(12);
        await Assert.That(warnings[0].GetProperty("detail").GetString()).IsEqualTo("Total 5 vs 7");
    }

    [Test]
    public async Task FormatSnapshot_NoWarnings_OmitsWarningsField()
    {
        // Absent-key-when-clean contract holds on the snapshot exactly like on Format.
        var snapshot = new CoverageSnapshot(
            CommitSha: "x", Branch: "y", Project: "z",
            Timestamp: DateTimeOffset.UnixEpoch, FileHash: null, Report: Reports.Mixed);

        var json = JsonFormatter.FormatSnapshot(snapshot);

        await Assert.That(JsonDocument.Parse(json).RootElement.TryGetProperty("warnings", out _)).IsFalse();
    }

    // ── Warnings: same null-omission contract as `lineChanges` / `uncoveredLines` ──

    [Test]
    public async Task Format_NoWarnings_OmitsWarningsField()
    {
        // Empty Warnings → null → field absent. Same contract as `lineChanges` /
        // `uncoveredLines`. Consumers can detect a clean report with TryGetProperty.
        var json = JsonFormatter.Format(Reports.Mixed);
        var root = JsonDocument.Parse(json).RootElement;

        await Assert.That(root.TryGetProperty("warnings", out _)).IsFalse();
    }

    [Test]
    public async Task Format_WithWarnings_SerializesArrayWithKindFileLineDetail()
    {
        // Each warning round-trips with the four-field shape. Pin the camelCased field
        // names so the public JSON contract is asserted explicitly.
        var report = new CoverageReport([new FileCoverage("src/A.cs", 1, 1, 0, 0)])
        {
            Warnings =
            [
                new CoverageWarning(CoverageWarningKind.BranchTotalMismatch, "src/A.cs", 12, "Total 5 vs 7"),
                new CoverageWarning(CoverageWarningKind.MalformedConditionCoverage, "src/B.cs", 30, "raw")
            ]
        };

        var json = JsonFormatter.Format(report);
        var warnings = JsonDocument.Parse(json).RootElement.GetProperty("warnings").EnumerateArray().ToList();

        await Assert.That(warnings.Count).IsEqualTo(2);
        await Assert.That(warnings[0].GetProperty("kind").GetString()).IsEqualTo("BranchTotalMismatch");
        await Assert.That(warnings[0].GetProperty("file").GetString()).IsEqualTo("src/A.cs");
        await Assert.That(warnings[0].GetProperty("line").GetInt32()).IsEqualTo(12);
        await Assert.That(warnings[0].GetProperty("detail").GetString()).IsEqualTo("Total 5 vs 7");
        await Assert.That(warnings[1].GetProperty("kind").GetString()).IsEqualTo("MalformedConditionCoverage");
    }
}