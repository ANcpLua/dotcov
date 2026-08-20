using DotCov.Tests.Infrastructure;
using Xunit;

namespace DotCov.Tests;

public sealed class CoverageSnapshotTests
{
    [Fact]
    public void Record_PreservesAllConstructorArguments()
    {
        var report = Reports.Mixed;
        var ts = new DateTimeOffset(2026, 5, 11, 12, 0, 0, TimeSpan.Zero);

        var snapshot = new CoverageSnapshot(
            CommitSha: "abc123",
            Branch: "main",
            Project: "MyApp",
            Timestamp: ts,
            FileHash: "deadbeef",
            Report: report);

        Assert.Equal("abc123", snapshot.CommitSha);
        Assert.Equal("main", snapshot.Branch);
        Assert.Equal("MyApp", snapshot.Project);
        Assert.Equal(ts, snapshot.Timestamp);
        Assert.Equal("deadbeef", snapshot.FileHash);
        Assert.Same(report, snapshot.Report);
    }

    [Fact]
    public void Snapshot_Timestamp_SerializesAsRoundTrippableIso8601()
    {
        // The payload is POSTed to arbitrary endpoints, so the timestamp's wire shape is a
        // contract: ISO 8601 with explicit offset, parseable back to the exact instant —
        // never a locale-dependent or offset-less rendering.
        var ts = new DateTimeOffset(2026, 5, 11, 12, 30, 45, TimeSpan.FromHours(2));
        var snapshot = new CoverageSnapshot("abc123", "main", "MyApp", ts, null, CoverageReport.Empty);

        var json = DotCov.Formatters.JsonFormatter.FormatSnapshot(snapshot);

        Assert.Contains("\"timestamp\": \"2026-05-11T12:30:45+02:00\"", json);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var roundTripped = doc.RootElement.GetProperty("timestamp").GetDateTimeOffset();
        Assert.Equal(ts, roundTripped);
    }
}

public sealed class FileHasherTests : IDisposable
{
    private readonly string _tempFile = Path.GetTempFileName();

    public void Dispose()
    {
        if (File.Exists(_tempFile)) File.Delete(_tempFile);
    }

    [Fact]
    public void ComputeHash_KnownContent_MatchesExpectedSha256()
    {
        File.WriteAllText(_tempFile, "abc");

        var hash = FileHasher.ComputeHash(_tempFile);

        // SHA-256("abc")
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", hash);
    }

    [Fact]
    public void ComputeHash_EmptyFile_MatchesEmptySha256()
    {
        File.WriteAllText(_tempFile, string.Empty);

        var hash = FileHasher.ComputeHash(_tempFile);

        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", hash);
    }

    [Fact]
    public void ComputeHash_SameContent_ProducesSameHash()
    {
        File.WriteAllText(_tempFile, "hello world");
        var first = FileHasher.ComputeHash(_tempFile);

        File.WriteAllText(_tempFile, "hello world");
        var second = FileHasher.ComputeHash(_tempFile);

        Assert.Equal(first, second);
    }

    [Fact]
    public void ComputeHash_DifferentContent_ProducesDifferentHash()
    {
        File.WriteAllText(_tempFile, "hello");
        var first = FileHasher.ComputeHash(_tempFile);

        File.WriteAllText(_tempFile, "world");
        var second = FileHasher.ComputeHash(_tempFile);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ComputeHash_OutputIsLowercaseHex()
    {
        File.WriteAllText(_tempFile, "test");

        var hash = FileHasher.ComputeHash(_tempFile);

        Assert.Matches("^[0-9a-f]{64}$", hash);
    }
}
