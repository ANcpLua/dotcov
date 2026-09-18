using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

public sealed class CoverageSnapshotTests
{
    [Test]
    public async Task Record_PreservesAllConstructorArguments()
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

        await Assert.That(snapshot.CommitSha).IsEqualTo("abc123");
        await Assert.That(snapshot.Branch).IsEqualTo("main");
        await Assert.That(snapshot.Project).IsEqualTo("MyApp");
        await Assert.That(snapshot.Timestamp).IsEqualTo(ts);
        await Assert.That(snapshot.FileHash).IsEqualTo("deadbeef");
        await Assert.That(snapshot.Report).IsSameReferenceAs(report);
    }

    [Test]
    public async Task Snapshot_Timestamp_SerializesAsRoundTrippableIso8601()
    {
        // The payload is POSTed to arbitrary endpoints, so the timestamp's wire shape is a
        // contract: ISO 8601 with explicit offset, parseable back to the exact instant —
        // never a locale-dependent or offset-less rendering.
        var ts = new DateTimeOffset(2026, 5, 11, 12, 30, 45, TimeSpan.FromHours(2));
        var snapshot = new CoverageSnapshot("abc123", "main", "MyApp", ts, null, CoverageReport.Empty);

        var json = DotCov.Formatters.JsonFormatter.FormatSnapshot(snapshot);

        await Assert.That(json).Contains("\"timestamp\": \"2026-05-11T12:30:45+02:00\"");

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var roundTripped = doc.RootElement.GetProperty("timestamp").GetDateTimeOffset();
        await Assert.That(roundTripped).IsEqualTo(ts);
    }
}

public sealed class FileHasherTests : IDisposable
{
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-hash-");
    private string TempFile => _ws.PathOf("content.txt");

    public void Dispose() => _ws.Dispose();

    [Test]
    public async Task ComputeHash_KnownContent_MatchesExpectedSha256()
    {
        await File.WriteAllTextAsync(TempFile, "abc");

        var hash = FileHasher.ComputeHash(TempFile);

        // SHA-256("abc")
        await Assert.That(hash).IsEqualTo("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }

    [Test]
    public async Task ComputeHash_EmptyFile_MatchesEmptySha256()
    {
        await File.WriteAllTextAsync(TempFile, string.Empty);

        var hash = FileHasher.ComputeHash(TempFile);

        await Assert.That(hash).IsEqualTo("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    }

    [Test]
    public async Task ComputeHash_SameContent_ProducesSameHash()
    {
        await File.WriteAllTextAsync(TempFile, "hello world");
        var first = FileHasher.ComputeHash(TempFile);

        await File.WriteAllTextAsync(TempFile, "hello world");
        var second = FileHasher.ComputeHash(TempFile);

        await Assert.That(second).IsEqualTo(first);
    }

    [Test]
    public async Task ComputeHash_DifferentContent_ProducesDifferentHash()
    {
        await File.WriteAllTextAsync(TempFile, "hello");
        var first = FileHasher.ComputeHash(TempFile);

        await File.WriteAllTextAsync(TempFile, "world");
        var second = FileHasher.ComputeHash(TempFile);

        await Assert.That(second).IsNotEqualTo(first);
    }

    [Test]
    public async Task ComputeHash_OutputIsLowercaseHex()
    {
        await File.WriteAllTextAsync(TempFile, "test");

        var hash = FileHasher.ComputeHash(TempFile);

        await Assert.That(hash).Matches("^[0-9a-f]{64}$");
    }
}
