using System.Text.Json;
using DotCov.Formatters;
using DotCov.Tests.Infrastructure;
using Xunit;

namespace DotCov.Tests;

public sealed class JsonFormatterTests
{
    [Fact]
    public void Format_ProducesValidJson()
    {
        var json = JsonFormatter.Format(Reports.Mixed);

        var parsed = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Object, parsed.RootElement.GetProperty("summary").ValueKind);
        Assert.Equal(JsonValueKind.Array, parsed.RootElement.GetProperty("files").ValueKind);
    }

    [Fact]
    public void Format_Summary_RoundsRatesToTwoDecimals()
    {
        var json = JsonFormatter.Format(Reports.Mixed);
        var summary = JsonDocument.Parse(json).RootElement.GetProperty("summary");

        // 7/12 lines = 58.3333… → rounded to 58.33
        Assert.Equal(58.33, summary.GetProperty("lineRate").GetDouble());
    }

    [Fact]
    public void Format_WithBranchData_SetsHasBranchDataTrue()
    {
        var json = JsonFormatter.Format(Reports.Mixed);
        var summary = JsonDocument.Parse(json).RootElement.GetProperty("summary");

        Assert.True(summary.GetProperty("hasBranchData").GetBoolean());
        Assert.Equal(JsonValueKind.Number, summary.GetProperty("branchRate").ValueKind);
    }

    [Fact]
    public void Format_NoBranchData_SetsHasBranchDataFalseAndOmitsBranchRate()
    {
        var json = JsonFormatter.Format(Reports.LinesOnly);
        var summary = JsonDocument.Parse(json).RootElement.GetProperty("summary");

        Assert.False(summary.GetProperty("hasBranchData").GetBoolean());
        // Null values are omitted (DefaultIgnoreCondition.WhenWritingNull) — that's the contract.
        Assert.False(summary.TryGetProperty("branchRate", out _));
    }

    [Fact]
    public void Format_PerFileBranchRate_IsOmittedWhenNoBranches()
    {
        var json = JsonFormatter.Format(Reports.Mixed);
        var files = JsonDocument.Parse(json).RootElement.GetProperty("files");

        var unused = files.EnumerateArray().Single(f => f.GetProperty("path").GetString() == "src/Unused.cs");
        Assert.False(unused.TryGetProperty("branchRate", out _));
    }

    [Fact]
    public void Format_OmitsUncoveredLines_WhenList_IsEmpty()
    {
        var report = new CoverageReport([
            new FileCoverage("a.cs", 1, 1, 0, 0) { UncoveredLines = [] }
        ]);

        var json = JsonFormatter.Format(report);
        var file = JsonDocument.Parse(json).RootElement.GetProperty("files")[0];

        Assert.Throws<KeyNotFoundException>(() => file.GetProperty("uncoveredLines"));
    }

    [Fact]
    public void Format_IncludesUncoveredLines_WhenPopulated()
    {
        var report = new CoverageReport([
            new FileCoverage("a.cs", 1, 3, 0, 0) { UncoveredLines = [10, 20, 30] }
        ]);

        var json = JsonFormatter.Format(report);
        var file = JsonDocument.Parse(json).RootElement.GetProperty("files")[0];
        var uncovered = file.GetProperty("uncoveredLines").EnumerateArray().Select(e => e.GetInt32()).ToArray();

        Assert.Equal([10, 20, 30], uncovered);
    }

    [Fact]
    public void Format_IncludesPartialBranches_WhenPopulated()
    {
        var report = new CoverageReport([
            new FileCoverage("a.cs", 1, 1, 1, 2) { PartialBranches = [new BranchDetail(15, 1, 2)] }
        ]);

        var json = JsonFormatter.Format(report);
        var file = JsonDocument.Parse(json).RootElement.GetProperty("files")[0];
        var partial = file.GetProperty("partialBranches")[0];

        Assert.Equal(15, partial.GetProperty("line").GetInt32());
        Assert.Equal(1, partial.GetProperty("covered").GetInt32());
        Assert.Equal(2, partial.GetProperty("total").GetInt32());
    }

    [Fact]
    public void FormatDiff_ProducesValidJsonWithSummaryAndFiles()
    {
        var diff = CoverageDiff.Compare(
            new CoverageReport([new FileCoverage("a.cs", 5, 10, 0, 0)]),
            new CoverageReport([new FileCoverage("a.cs", 8, 10, 0, 0)]));

        var json = JsonFormatter.FormatDiff(diff);
        var root = JsonDocument.Parse(json).RootElement;

        Assert.Equal(50.0, root.GetProperty("summary").GetProperty("before").GetDouble());
        Assert.Equal(80.0, root.GetProperty("summary").GetProperty("after").GetDouble());
        Assert.Equal(30.0, root.GetProperty("summary").GetProperty("delta").GetDouble());
    }

    [Fact]
    public void FormatDiff_NullBefore_IsOmittedFromOutput()
    {
        var diff = CoverageDiff.Compare(
            CoverageReport.Empty,
            new CoverageReport([new FileCoverage("new.cs", 5, 10, 0, 0)]));

        var json = JsonFormatter.FormatDiff(diff);
        var file = JsonDocument.Parse(json).RootElement.GetProperty("files")[0];

        // Same null-omission contract as the report serializer.
        Assert.False(file.TryGetProperty("before", out _));
        Assert.Equal("added", file.GetProperty("change").GetString());
    }

    [Fact]
    public void FormatDiff_NullAfter_IsOmittedFromOutput()
    {
        var diff = CoverageDiff.Compare(
            new CoverageReport([new FileCoverage("gone.cs", 4, 5, 0, 0)]),
            CoverageReport.Empty);

        var json = JsonFormatter.FormatDiff(diff);
        var file = JsonDocument.Parse(json).RootElement.GetProperty("files")[0];

        Assert.False(file.TryGetProperty("after", out _));
        Assert.Equal(80.0, file.GetProperty("before").GetDouble());
        Assert.Equal("removed", file.GetProperty("change").GetString());
    }

    [Fact]
    public void FormatDiff_IndirectLineChanges_AppearInJsonPayload()
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

        Assert.Equal(1, root.GetProperty("summary").GetProperty("indirectLineChanges").GetInt32());

        var lineChange = root.GetProperty("files")[0].GetProperty("lineChanges")[0];
        Assert.Equal(10, lineChange.GetProperty("line").GetInt32());
        Assert.Equal("newlymissed", lineChange.GetProperty("change").GetString());
        Assert.Equal(5, lineChange.GetProperty("beforeHits").GetInt32());
        Assert.Equal(0, lineChange.GetProperty("afterHits").GetInt32());
    }

    [Fact]
    public void FormatDiff_AddedLine_EmitsAddedChangeWithOnlyAfterHits()
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

        Assert.Equal(30, lineChange.GetProperty("line").GetInt32());
        Assert.Equal("added", lineChange.GetProperty("change").GetString());
        Assert.Equal(7, lineChange.GetProperty("afterHits").GetInt32());
        Assert.False(lineChange.TryGetProperty("beforeHits", out _));
    }

    [Fact]
    public void FormatDiff_RemovedLine_EmitsRemovedChangeWithOnlyBeforeHits()
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

        Assert.Equal(20, lineChange.GetProperty("line").GetInt32());
        Assert.Equal("removed", lineChange.GetProperty("change").GetString());
        Assert.Equal(4, lineChange.GetProperty("beforeHits").GetInt32());
        Assert.False(lineChange.TryGetProperty("afterHits", out _));
    }

    [Fact]
    public void FormatDiff_NewlyHitLine_EmitsNewlyHitChangeWithBothHits()
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

        Assert.Equal(10, lineChange.GetProperty("line").GetInt32());
        Assert.Equal("newlyhit", lineChange.GetProperty("change").GetString());
        Assert.Equal(0, lineChange.GetProperty("beforeHits").GetInt32());
        Assert.Equal(3, lineChange.GetProperty("afterHits").GetInt32());
    }

    [Fact]
    public void FormatDiff_NoIndirectChanges_LineChangesAbsent()
    {
        var diff = CoverageDiff.Compare(
            new CoverageReport([new FileCoverage("a.cs", 5, 10, 0, 0)]),
            new CoverageReport([new FileCoverage("a.cs", 8, 10, 0, 0)]));

        var json = JsonFormatter.FormatDiff(diff);
        var file = JsonDocument.Parse(json).RootElement.GetProperty("files")[0];

        Assert.False(file.TryGetProperty("lineChanges", out _));
    }

    [Fact]
    public void FormatSnapshot_IncludesAllMetadata()
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

        Assert.Equal("abc123", root.GetProperty("commit").GetString());
        Assert.Equal("main", root.GetProperty("branch").GetString());
        Assert.Equal("MyApp", root.GetProperty("project").GetString());
        Assert.Equal("deadbeef", root.GetProperty("fileHash").GetString());
        Assert.Equal(JsonValueKind.Object, root.GetProperty("summary").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("files").ValueKind);
    }

    [Fact]
    public void FormatSnapshot_NullFileHash_IsOmitted()
    {
        var snapshot = new CoverageSnapshot(
            CommitSha: "x", Branch: "y", Project: "z",
            Timestamp: DateTimeOffset.UnixEpoch, FileHash: null,
            Report: CoverageReport.Empty);

        var json = JsonFormatter.FormatSnapshot(snapshot);
        var root = JsonDocument.Parse(json).RootElement;

        Assert.Throws<KeyNotFoundException>(() => root.GetProperty("fileHash"));
    }

    [Fact]
    public void Format_UsesCamelCasePropertyNames()
    {
        var json = JsonFormatter.Format(Reports.Mixed);

        Assert.Contains("\"lineRate\"", json);
        Assert.Contains("\"branchRate\"", json);
        Assert.Contains("\"totalLines\"", json);
        Assert.Contains("\"coveredLines\"", json);
    }

    // ── Warnings: same null-omission contract as `lineChanges` / `uncoveredLines` ──

    [Fact]
    public void Format_NoWarnings_OmitsWarningsField()
    {
        // Empty Warnings → null → field absent. Same contract as `lineChanges` /
        // `uncoveredLines`. Consumers can detect a clean report with TryGetProperty.
        var json = JsonFormatter.Format(Reports.Mixed);
        var root = JsonDocument.Parse(json).RootElement;

        Assert.False(root.TryGetProperty("warnings", out _));
    }

    [Fact]
    public void Format_WithWarnings_SerializesArrayWithKindFileLineDetail()
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

        Assert.Equal(2, warnings.Count);
        Assert.Equal("BranchTotalMismatch", warnings[0].GetProperty("kind").GetString());
        Assert.Equal("src/A.cs", warnings[0].GetProperty("file").GetString());
        Assert.Equal(12, warnings[0].GetProperty("line").GetInt32());
        Assert.Equal("Total 5 vs 7", warnings[0].GetProperty("detail").GetString());
        Assert.Equal("MalformedConditionCoverage", warnings[1].GetProperty("kind").GetString());
    }
}
