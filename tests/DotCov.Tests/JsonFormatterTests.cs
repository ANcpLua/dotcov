using System.Text.Json;
using DotCov.Formatters;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;
[TestClass]
public sealed class JsonFormatterTests
{
    [TestMethod]
    public void Format_ProducesValidJson()
    {
        var json = JsonFormatter.Format(Reports.Mixed);
        var parsed = JsonDocument.Parse(json);
        Assert.AreEqual(JsonValueKind.Object, parsed.RootElement.GetProperty("summary").ValueKind);
        Assert.AreEqual(JsonValueKind.Array, parsed.RootElement.GetProperty("files").ValueKind);
    }

    [TestMethod]
    public void Format_Summary_RoundsRatesToTwoDecimals()
    {
        var json = JsonFormatter.Format(Reports.Mixed);
        var summary = JsonDocument.Parse(json).RootElement.GetProperty("summary");
        // 7/12 lines = 58.3333… → rounded to 58.33
        Assert.AreEqual(58.33, summary.GetProperty("lineRate").GetDouble());
    }

    [TestMethod]
    public void Format_WithBranchData_SetsHasBranchDataTrue()
    {
        var json = JsonFormatter.Format(Reports.Mixed);
        var summary = JsonDocument.Parse(json).RootElement.GetProperty("summary");
        Assert.IsTrue(summary.GetProperty("hasBranchData").GetBoolean());
        Assert.AreEqual(JsonValueKind.Number, summary.GetProperty("branchRate").ValueKind);
    }

    [TestMethod]
    public void Format_NoBranchData_SetsHasBranchDataFalseAndOmitsBranchRate()
    {
        var json = JsonFormatter.Format(Reports.LinesOnly);
        var summary = JsonDocument.Parse(json).RootElement.GetProperty("summary");
        Assert.IsFalse(summary.GetProperty("hasBranchData").GetBoolean());
        // A null rate is omitted, not written as null — that's the contract.
        Assert.IsFalse(summary.TryGetProperty("branchRate", out _));
    }

    [TestMethod]
    public void Format_PerFileBranchRate_IsOmittedWhenNoBranches()
    {
        var json = JsonFormatter.Format(Reports.Mixed);
        var files = JsonDocument.Parse(json).RootElement.GetProperty("files");
        var unused = files.EnumerateArray().Single(f => f.GetProperty("path").GetString() == "src/Unused.cs");
        Assert.IsFalse(unused.TryGetProperty("branchRate", out _));
    }

    [TestMethod]
    public void Format_OmitsUncoveredLines_WhenList_IsEmpty()
    {
        var report = new CoverageReport([new FileCoverage("a.cs", 1, 1, 0, 0) { UncoveredLines = [] }]);
        var json = JsonFormatter.Format(report);
        var file = JsonDocument.Parse(json).RootElement.GetProperty("files")[0];
        Assert.ThrowsExactly<KeyNotFoundException>(() => file.GetProperty("uncovered"));
    }

    [TestMethod]
    [DataRow(new[] { 10, 20, 30 }, "10,20,30")]
    [DataRow(new[] { 10, 11, 12, 20, 30, 31 }, "10-12,20,30-31")]
    // A hand-built list need not be sorted or unique; the ranges still are.
    [DataRow(new[] { 31, 10, 11, 11, 30 }, "10-11,30-31")]
    public void Format_SpellsUncoveredLinesAsRanges_WhenPopulated(int[] lines, string expected)
    {
        var report = new CoverageReport([new FileCoverage("a.cs", 1, 3, 0, 0) { UncoveredLines = lines }]);
        var json = JsonFormatter.Format(report);
        var file = JsonDocument.Parse(json).RootElement.GetProperty("files")[0];
        Assert.AreEqual(expected, file.GetProperty("uncovered").GetString());
        Assert.IsFalse(file.TryGetProperty("uncoveredLines", out _));
    }

    [TestMethod]
    public void Format_IncludesPartialBranches_WhenPopulated()
    {
        var report = new CoverageReport([new FileCoverage("a.cs", 1, 1, 1, 2) { PartialBranches = [new BranchDetail(15, 1, 2)] }]);
        var json = JsonFormatter.Format(report);
        var file = JsonDocument.Parse(json).RootElement.GetProperty("files")[0];
        var partial = file.GetProperty("partialBranches")[0];
        Assert.AreEqual(15, partial.GetProperty("line").GetInt32());
        Assert.AreEqual(1, partial.GetProperty("covered").GetInt32());
        Assert.AreEqual(2, partial.GetProperty("total").GetInt32());
    }

    [TestMethod]
    public void Format_Summary_CarriesBranchTotals()
    {
        // totalBranches/coveredBranches are part of the wire contract — asserted by value
        // so the writer statements cannot be deleted wholesale. WriteSummary is shared, so
        // this also pins the snapshot summary path.
        var json = JsonFormatter.Format(Reports.Mixed);
        var summary = JsonDocument.Parse(json).RootElement.GetProperty("summary");
        Assert.AreEqual(6, summary.GetProperty("totalBranches").GetInt32());
        Assert.AreEqual(3, summary.GetProperty("coveredBranches").GetInt32());
    }

    [TestMethod]
    public void Format_PerFile_CarriesAllFourCountFields()
    {
        var json = JsonFormatter.Format(Reports.Mixed);
        var files = JsonDocument.Parse(json).RootElement.GetProperty("files");
        var parser = files.EnumerateArray().Single(f => f.GetProperty("path").GetString() == "src/Parser.cs");
        Assert.AreEqual(3, parser.GetProperty("linesHit").GetInt32());
        Assert.AreEqual(5, parser.GetProperty("linesTotal").GetInt32());
        Assert.AreEqual(1, parser.GetProperty("branchesHit").GetInt32());
        Assert.AreEqual(4, parser.GetProperty("branchesTotal").GetInt32());
    }

    [TestMethod]
    public void Format_EmptyPartialBranches_OmitsKey()
    {
        // Absent-key-means-clean: an empty partialBranches list must omit the key entirely,
        // same contract as uncoveredLines/warnings/lineChanges.
        var report = new CoverageReport([new FileCoverage("a.cs", 1, 1, 2, 2) { PartialBranches = [] }]);
        var json = JsonFormatter.Format(report);
        var file = JsonDocument.Parse(json).RootElement.GetProperty("files")[0];
        Assert.IsFalse(file.TryGetProperty("partialBranches", out _));
    }

    [TestMethod]
    public void FormatDiff_ProducesValidJsonWithSummaryAndFiles()
    {
        var diff = CoverageDiff.Compare(new CoverageReport([new FileCoverage("a.cs", 5, 10, 0, 0)]), new CoverageReport([new FileCoverage("a.cs", 8, 10, 0, 0)]));
        var json = JsonFormatter.FormatDiff(diff);
        var root = JsonDocument.Parse(json).RootElement;
        Assert.AreEqual(50.0, root.GetProperty("summary").GetProperty("before").GetDouble());
        Assert.AreEqual(80.0, root.GetProperty("summary").GetProperty("after").GetDouble());
        Assert.AreEqual(30.0, root.GetProperty("summary").GetProperty("delta").GetDouble());
    }

    [TestMethod]
    public void FormatDiff_NullBefore_IsOmittedFromOutput()
    {
        var diff = CoverageDiff.Compare(CoverageReport.Empty, new CoverageReport([new FileCoverage("new.cs", 5, 10, 0, 0)]));
        var json = JsonFormatter.FormatDiff(diff);
        var file = JsonDocument.Parse(json).RootElement.GetProperty("files")[0];
        // Same null-omission contract as the report serializer.
        Assert.IsFalse(file.TryGetProperty("before", out _));
        Assert.AreEqual("added", file.GetProperty("change").GetString());
    }

    [TestMethod]
    public void FormatDiff_NullAfter_IsOmittedFromOutput()
    {
        var diff = CoverageDiff.Compare(new CoverageReport([new FileCoverage("gone.cs", 4, 5, 0, 0)]), CoverageReport.Empty);
        var json = JsonFormatter.FormatDiff(diff);
        var file = JsonDocument.Parse(json).RootElement.GetProperty("files")[0];
        Assert.IsFalse(file.TryGetProperty("after", out _));
        Assert.AreEqual(80.0, file.GetProperty("before").GetDouble());
        Assert.AreEqual("removed", file.GetProperty("change").GetString());
    }

    [TestMethod]
    public void FormatDiff_IndirectLineChanges_AppearInJsonPayload()
    {
        var before = new CoverageReport([new FileCoverage("a.cs", 1, 1, 0, 0) { LineHits = new Dictionary<int, int> { [10] = 5 } }]);
        var after = new CoverageReport([new FileCoverage("a.cs", 0, 1, 0, 0) { LineHits = new Dictionary<int, int> { [10] = 0 } }]);
        var json = JsonFormatter.FormatDiff(CoverageDiff.Compare(before, after));
        var root = JsonDocument.Parse(json).RootElement;
        Assert.AreEqual(1, root.GetProperty("summary").GetProperty("indirectLineChanges").GetInt32());
        var lineChange = root.GetProperty("files")[0].GetProperty("lineChanges")[0];
        Assert.AreEqual(10, lineChange.GetProperty("line").GetInt32());
        Assert.AreEqual("newlymissed", lineChange.GetProperty("change").GetString());
        Assert.AreEqual(5, lineChange.GetProperty("beforeHits").GetInt32());
        Assert.AreEqual(0, lineChange.GetProperty("afterHits").GetInt32());
    }

    [TestMethod]
    public void FormatDiff_AddedLine_EmitsAddedChangeWithOnlyAfterHits()
    {
        // Same file on both sides; line 30 only exists in After → LineDelta.Added variant.
        // Wire format: change="added", beforeHits omitted (null), afterHits populated.
        var before = new CoverageReport([new FileCoverage("a.cs", 1, 1, 0, 0) { LineHits = new Dictionary<int, int> { [10] = 1 } }]);
        var after = new CoverageReport([new FileCoverage("a.cs", 1, 1, 0, 0) { LineHits = new Dictionary<int, int> { [10] = 1, [30] = 7 } }]);
        var json = JsonFormatter.FormatDiff(CoverageDiff.Compare(before, after));
        var lineChange = JsonDocument.Parse(json).RootElement.GetProperty("files")[0].GetProperty("lineChanges")[0];
        Assert.AreEqual(30, lineChange.GetProperty("line").GetInt32());
        Assert.AreEqual("added", lineChange.GetProperty("change").GetString());
        Assert.AreEqual(7, lineChange.GetProperty("afterHits").GetInt32());
        Assert.IsFalse(lineChange.TryGetProperty("beforeHits", out _));
    }

    [TestMethod]
    public void FormatDiff_RemovedLine_EmitsRemovedChangeWithOnlyBeforeHits()
    {
        // Line 20 dropped from After → LineDelta.Removed variant.
        // Wire format: change="removed", beforeHits populated, afterHits omitted (null).
        var before = new CoverageReport([new FileCoverage("a.cs", 1, 1, 0, 0) { LineHits = new Dictionary<int, int> { [10] = 1, [20] = 4 } }]);
        var after = new CoverageReport([new FileCoverage("a.cs", 1, 1, 0, 0) { LineHits = new Dictionary<int, int> { [10] = 1 } }]);
        var json = JsonFormatter.FormatDiff(CoverageDiff.Compare(before, after));
        var lineChange = JsonDocument.Parse(json).RootElement.GetProperty("files")[0].GetProperty("lineChanges")[0];
        Assert.AreEqual(20, lineChange.GetProperty("line").GetInt32());
        Assert.AreEqual("removed", lineChange.GetProperty("change").GetString());
        Assert.AreEqual(4, lineChange.GetProperty("beforeHits").GetInt32());
        Assert.IsFalse(lineChange.TryGetProperty("afterHits", out _));
    }

    [TestMethod]
    public void FormatDiff_NewlyHitLine_EmitsNewlyHitChangeWithBothHits()
    {
        // Line 10 missed before, hit now → LineDelta.NewlyHit variant.
        // Wire format: change="newlyhit", both beforeHits (0) and afterHits populated.
        var before = new CoverageReport([new FileCoverage("a.cs", 0, 1, 0, 0) { LineHits = new Dictionary<int, int> { [10] = 0 } }]);
        var after = new CoverageReport([new FileCoverage("a.cs", 1, 1, 0, 0) { LineHits = new Dictionary<int, int> { [10] = 3 } }]);
        var json = JsonFormatter.FormatDiff(CoverageDiff.Compare(before, after));
        var lineChange = JsonDocument.Parse(json).RootElement.GetProperty("files")[0].GetProperty("lineChanges")[0];
        Assert.AreEqual(10, lineChange.GetProperty("line").GetInt32());
        Assert.AreEqual("newlyhit", lineChange.GetProperty("change").GetString());
        Assert.AreEqual(0, lineChange.GetProperty("beforeHits").GetInt32());
        Assert.AreEqual(3, lineChange.GetProperty("afterHits").GetInt32());
    }

    [TestMethod]
    public void FormatDiff_NoIndirectChanges_LineChangesAbsent()
    {
        var diff = CoverageDiff.Compare(new CoverageReport([new FileCoverage("a.cs", 5, 10, 0, 0)]), new CoverageReport([new FileCoverage("a.cs", 8, 10, 0, 0)]));
        var json = JsonFormatter.FormatDiff(diff);
        var file = JsonDocument.Parse(json).RootElement.GetProperty("files")[0];
        Assert.IsFalse(file.TryGetProperty("lineChanges", out _));
    }

    [TestMethod]
    public void FormatDiff_ModifiedFile_CarriesPathAndAfterByValue()
    {
        var diff = CoverageDiff.Compare(new CoverageReport([new FileCoverage("a.cs", 5, 10, 0, 0)]), new CoverageReport([new FileCoverage("a.cs", 8, 10, 0, 0)]));
        var json = JsonFormatter.FormatDiff(diff);
        var file = JsonDocument.Parse(json).RootElement.GetProperty("files")[0];
        // path and after asserted by value, so neither writer statement can be deleted unnoticed.
        Assert.AreEqual("a.cs", file.GetProperty("path").GetString());
        Assert.AreEqual(80.0, file.GetProperty("after").GetDouble());
        Assert.AreEqual("modified", file.GetProperty("change").GetString());
    }

    [TestMethod]
    public void FormatSnapshot_FilesArray_CarriesPerFileCounts()
    {
        // The snapshot's files loop must actually run: an empty "files" array satisfies a
        // ValueKind.Array check, so pin the length and the first file's counts by value.
        var snapshot = new CoverageSnapshot(CommitSha: "abc123", Branch: "main", Project: "App", Timestamp: DateTimeOffset.UnixEpoch, FileHash: null, Report: Reports.Mixed);
        var json = JsonFormatter.FormatSnapshot(snapshot);
        var files = JsonDocument.Parse(json).RootElement.GetProperty("files");
        Assert.AreEqual(3, files.GetArrayLength());
        var calc = files.EnumerateArray().Single(f => f.GetProperty("path").GetString() == "src/Calculator.cs");
        Assert.AreEqual(4, calc.GetProperty("linesHit").GetInt32());
        Assert.AreEqual(4, calc.GetProperty("linesTotal").GetInt32());
        Assert.AreEqual(2, calc.GetProperty("branchesHit").GetInt32());
        Assert.AreEqual(2, calc.GetProperty("branchesTotal").GetInt32());
    }

    [TestMethod]
    public void FormatSnapshot_IncludesAllMetadata()
    {
        var snapshot = new CoverageSnapshot(CommitSha: "abc123", Branch: "main", Project: "MyApp", Timestamp: new DateTimeOffset(2026, 5, 11, 12, 0, 0, TimeSpan.Zero), FileHash: "deadbeef", Report: Reports.FullyCovered);
        var json = JsonFormatter.FormatSnapshot(snapshot);
        var root = JsonDocument.Parse(json).RootElement;
        Assert.AreEqual("abc123", root.GetProperty("commit").GetString());
        Assert.AreEqual("main", root.GetProperty("branch").GetString());
        Assert.AreEqual("MyApp", root.GetProperty("project").GetString());
        Assert.AreEqual("deadbeef", root.GetProperty("fileHash").GetString());
        Assert.AreEqual(JsonValueKind.Object, root.GetProperty("summary").ValueKind);
        Assert.AreEqual(JsonValueKind.Array, root.GetProperty("files").ValueKind);
    }

    [TestMethod]
    public void FormatSnapshot_NullFileHash_IsOmitted()
    {
        var snapshot = new CoverageSnapshot(CommitSha: "x", Branch: "y", Project: "z", Timestamp: DateTimeOffset.UnixEpoch, FileHash: null, Report: CoverageReport.Empty);
        var json = JsonFormatter.FormatSnapshot(snapshot);
        var root = JsonDocument.Parse(json).RootElement;
        Assert.ThrowsExactly<KeyNotFoundException>(() => root.GetProperty("fileHash"));
    }

    [TestMethod]
    public void Format_UsesCamelCasePropertyNames()
    {
        var json = JsonFormatter.Format(Reports.Mixed);
        Assert.Contains("\"lineRate\"", json);
        Assert.Contains("\"branchRate\"", json);
        Assert.Contains("\"totalLines\"", json);
        Assert.Contains("\"coveredLines\"", json);
    }

    [TestMethod]
    public void FormatSnapshot_WithWarnings_IncludesWarningsArray()
    {
        // The snapshot is the payload built for remote sinks — the anomaly channel must
        // survive the pipeline boundary, with the same shape Format emits.
        var report = new CoverageReport([new FileCoverage("src/A.cs", 1, 1, 0, 0)])
        {
            Warnings = [new CoverageWarning(CoverageWarningKind.BranchTotalMismatch, "src/A.cs", 12, "Total 5 vs 7")]
        };
        var snapshot = new CoverageSnapshot(CommitSha: "abc123", Branch: "main", Project: "App", Timestamp: DateTimeOffset.UnixEpoch, FileHash: null, Report: report);
        var json = JsonFormatter.FormatSnapshot(snapshot);
        var warnings = JsonDocument.Parse(json).RootElement.GetProperty("warnings");
        Assert.AreEqual(1, warnings.GetArrayLength());
        Assert.AreEqual("BranchTotalMismatch", warnings[0].GetProperty("kind").GetString());
        Assert.AreEqual("src/A.cs", warnings[0].GetProperty("file").GetString());
        Assert.AreEqual(12, warnings[0].GetProperty("line").GetInt32());
        Assert.AreEqual("Total 5 vs 7", warnings[0].GetProperty("detail").GetString());
    }

    [TestMethod]
    public void FormatSnapshot_NoWarnings_WritesAnEmptyWarningsArray()
    {
        // The envelope's keys are always present on the snapshot, exactly like on Format.
        var snapshot = new CoverageSnapshot(CommitSha: "x", Branch: "y", Project: "z", Timestamp: DateTimeOffset.UnixEpoch, FileHash: null, Report: Reports.Mixed);
        var json = JsonFormatter.FormatSnapshot(snapshot);
        Assert.AreEqual(0, JsonDocument.Parse(json).RootElement.GetProperty("warnings").GetArrayLength());
    }

    // ── Warnings: part of the envelope, always present ──
    [TestMethod]
    public void Format_NoWarnings_WritesAnEmptyWarningsArray()
    {
        // Empty Warnings → an empty array: a consumer reads the key without probing for it.
        var json = JsonFormatter.Format(Reports.Mixed);
        var root = JsonDocument.Parse(json).RootElement;
        Assert.AreEqual(0, root.GetProperty("warnings").GetArrayLength());
        Assert.AreEqual(0, root.GetProperty("inputs").GetArrayLength());
    }

    [TestMethod]
    public void Format_WithWarnings_SerializesArrayWithKindFileLineDetail()
    {
        // Each warning round-trips with the four-field shape. Pin the camelCased field
        // names so the public JSON contract is asserted explicitly.
        var report = new CoverageReport([new FileCoverage("src/A.cs", 1, 1, 0, 0)])
        {
            Warnings = [new CoverageWarning(CoverageWarningKind.BranchTotalMismatch, "src/A.cs", 12, "Total 5 vs 7"), new CoverageWarning(CoverageWarningKind.MalformedConditionCoverage, "src/B.cs", 30, "raw")]
        };
        var json = JsonFormatter.Format(report);
        var warnings = JsonDocument.Parse(json).RootElement.GetProperty("warnings").EnumerateArray().ToList();
        Assert.AreEqual(2, warnings.Count);
        Assert.AreEqual("BranchTotalMismatch", warnings[0].GetProperty("kind").GetString());
        Assert.AreEqual("src/A.cs", warnings[0].GetProperty("file").GetString());
        Assert.AreEqual(12, warnings[0].GetProperty("line").GetInt32());
        Assert.AreEqual("Total 5 vs 7", warnings[0].GetProperty("detail").GetString());
        Assert.AreEqual("MalformedConditionCoverage", warnings[1].GetProperty("kind").GetString());
    }
}
