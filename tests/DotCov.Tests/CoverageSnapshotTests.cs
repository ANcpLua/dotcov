using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;
[TestClass]
public sealed class CoverageSnapshotTests
{
    [TestMethod]
    public void Record_PreservesAllConstructorArguments()
    {
        var report = Reports.Mixed;
        var ts = new DateTimeOffset(2026, 5, 11, 12, 0, 0, TimeSpan.Zero);
        var snapshot = new CoverageSnapshot(CommitSha: "abc123", Branch: "main", Project: "MyApp", Timestamp: ts, FileHash: "deadbeef", Report: report);
        Assert.AreEqual("abc123", snapshot.CommitSha);
        Assert.AreEqual("main", snapshot.Branch);
        Assert.AreEqual("MyApp", snapshot.Project);
        Assert.AreEqual(ts, snapshot.Timestamp);
        Assert.AreEqual("deadbeef", snapshot.FileHash);
        Assert.AreSame(report, snapshot.Report);
    }

    [TestMethod]
    public void Snapshot_Timestamp_SerializesAsRoundTrippableIso8601()
    {
        // The payload is POSTed to arbitrary endpoints, so the timestamp's wire shape is a
        // contract: ISO 8601 with explicit offset, parseable back to the exact instant —
        // never a locale-dependent or offset-less rendering.
        var ts = new DateTimeOffset(2026, 5, 11, 12, 30, 45, TimeSpan.FromHours(2));
        var snapshot = new CoverageSnapshot("abc123", "main", "MyApp", ts, null, CoverageReport.Empty);
        var json = Formatters.JsonFormatter.FormatSnapshot(snapshot);
        Assert.Contains("\"timestamp\": \"2026-05-11T12:30:45+02:00\"", json);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var roundTripped = doc.RootElement.GetProperty("timestamp").GetDateTimeOffset();
        Assert.AreEqual(ts, roundTripped);
    }
}

[TestClass]
public sealed class FileHasherTests : IDisposable
{
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-hash-");
    private string TempFile => _ws.PathOf("content.txt");

    public void Dispose() => _ws.Dispose();
    [TestMethod]
    public async Task ComputeHash_KnownContent_MatchesExpectedSha256()
    {
        await File.WriteAllTextAsync(TempFile, "abc");
        var hash = FileHasher.ComputeHash(TempFile);
        // SHA-256("abc")
        Assert.AreEqual("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hash);
    }

    [TestMethod]
    public async Task ComputeHash_EmptyFile_MatchesEmptySha256()
    {
        await File.WriteAllTextAsync(TempFile, string.Empty);
        var hash = FileHasher.ComputeHash(TempFile);
        Assert.AreEqual("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", hash);
    }

    [TestMethod]
    public async Task ComputeHash_SameContent_ProducesSameHash()
    {
        await File.WriteAllTextAsync(TempFile, "hello world");
        var first = FileHasher.ComputeHash(TempFile);
        await File.WriteAllTextAsync(TempFile, "hello world");
        var second = FileHasher.ComputeHash(TempFile);
        Assert.AreEqual(first, second);
    }

    [TestMethod]
    public async Task ComputeHash_DifferentContent_ProducesDifferentHash()
    {
        await File.WriteAllTextAsync(TempFile, "hello");
        var first = FileHasher.ComputeHash(TempFile);
        await File.WriteAllTextAsync(TempFile, "world");
        var second = FileHasher.ComputeHash(TempFile);
        Assert.AreNotEqual(first, second);
    }

    [TestMethod]
    public async Task ComputeHash_OutputIsLowercaseHex()
    {
        await File.WriteAllTextAsync(TempFile, "test");
        var hash = FileHasher.ComputeHash(TempFile);
        Assert.MatchesRegex("^[0-9a-f]{64}$", hash);
    }
}
