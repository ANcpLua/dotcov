using System.Text.Json;
using DotCov.Formatters;

namespace DotCov.Tests;

/// <summary>
/// JSON shape for files the report names but never measured: the absent-key contract applies
/// per file exactly as it does to the summary.
/// </summary>
public sealed class JsonFormatterEdgeTests
{
    [Test]
    public async Task Format_FileWithoutLineData_OmitsItsLineRate_AndKeepsItsCounts()
    {
        // An unmeasured file (LineRate null) has no rate to report: the key is omitted, not
        // written as 0, while its zero counts stay explicit.
        var report = new CoverageReport([
            new FileCoverage("src/Empty.cs", 0, 0, 0, 0),
            new FileCoverage("src/A.cs", 1, 2, 0, 0)
        ]);

        var files = JsonDocument.Parse(JsonFormatter.Format(report)).RootElement.GetProperty("files");

        var empty = files[0];
        await Assert.That(empty.GetProperty("path").GetString()).IsEqualTo("src/Empty.cs");
        await Assert.That(empty.TryGetProperty("lineRate", out _)).IsFalse();
        await Assert.That(empty.TryGetProperty("branchRate", out _)).IsFalse();
        await Assert.That(empty.GetProperty("linesHit").GetInt32()).IsEqualTo(0);
        await Assert.That(empty.GetProperty("linesTotal").GetInt32()).IsEqualTo(0);

        var measured = files[1];
        await Assert.That(measured.GetProperty("lineRate").GetDouble()).IsEqualTo(50.0);
    }

    [Test]
    public async Task FormatSnapshot_FileWithoutLineData_OmitsItsLineRate()
    {
        var snapshot = new CoverageSnapshot("abc", "main", "proj", DateTimeOffset.UnixEpoch, null,
            new CoverageReport([new FileCoverage("src/Empty.cs", 0, 0, 0, 0)]));

        var root = JsonDocument.Parse(JsonFormatter.FormatSnapshot(snapshot)).RootElement;

        var file = root.GetProperty("files")[0];
        await Assert.That(file.TryGetProperty("lineRate", out _)).IsFalse();
        await Assert.That(root.GetProperty("summary").TryGetProperty("lineRate", out _)).IsFalse();
    }
}
