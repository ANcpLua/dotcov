using System.Text.Json;
using DotCov.Formatters;

namespace DotCov.Tests;
/// <summary>
/// JSON shape for files the report names but never measured: the absent-key contract applies
/// per file exactly as it does to the summary.
/// </summary>
[TestClass]
public sealed class JsonFormatterEdgeTests
{
    [TestMethod]
    public void Format_FileWithoutLineData_OmitsItsLineRate_AndKeepsItsCounts()
    {
        // An unmeasured file (LineRate null) has no rate to report: the key is omitted, not
        // written as 0, while its zero counts stay explicit.
        var report = new CoverageReport([new FileCoverage("src/Empty.cs", 0, 0, 0, 0), new FileCoverage("src/A.cs", 1, 2, 0, 0)]);
        var files = JsonDocument.Parse(JsonFormatter.Format(report)).RootElement.GetProperty("files");
        var empty = files[0];
        Assert.AreEqual("src/Empty.cs", empty.GetProperty("path").GetString());
        Assert.IsFalse(empty.TryGetProperty("lineRate", out _));
        Assert.IsFalse(empty.TryGetProperty("branchRate", out _));
        Assert.AreEqual(0, empty.GetProperty("linesHit").GetInt32());
        Assert.AreEqual(0, empty.GetProperty("linesTotal").GetInt32());
        var measured = files[1];
        Assert.AreEqual(50.0, measured.GetProperty("lineRate").GetDouble());
    }

    [TestMethod]
    public void FormatSnapshot_FileWithoutLineData_OmitsItsLineRate()
    {
        var snapshot = new CoverageSnapshot("abc", "main", "proj", DateTimeOffset.UnixEpoch, null, new CoverageReport([new FileCoverage("src/Empty.cs", 0, 0, 0, 0)]));
        var root = JsonDocument.Parse(JsonFormatter.FormatSnapshot(snapshot)).RootElement;
        var file = root.GetProperty("files")[0];
        Assert.IsFalse(file.TryGetProperty("lineRate", out _));
        Assert.IsFalse(root.GetProperty("summary").TryGetProperty("lineRate", out _));
    }
}
