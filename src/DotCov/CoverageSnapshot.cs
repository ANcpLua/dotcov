using System.Security.Cryptography;

namespace DotCov;

/// <summary>
/// Pipeline-ready coverage payload. ~500 bytes per file.
/// POST this to any endpoint (qyl collector, CI artifact store, whatever).
/// </summary>
public sealed record CoverageSnapshot(
    string CommitSha,
    string Branch,
    string Project,
    DateTimeOffset Timestamp,
    string? FileHash,
    CoverageReport Report);

public static class FileHasher
{
    /// <summary>SHA256 hash of a file — used for dedup. Same file = same hash = skip re-upload.</summary>
    public static string ComputeHash(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexStringLower(hash);
    }
}
