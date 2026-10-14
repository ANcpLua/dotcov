using System.Diagnostics.CodeAnalysis;

namespace DotCov.Fallout;

/// <summary>
/// Writes the gate's JSON document to the <c>JsonPath</c> file. Rendering stays in DotCov; this
/// only creates the directory and writes, reporting a failure to the caller, which decides what
/// it costs — the same contract as <see cref="CoverageBadge"/>.
/// </summary>
internal static class CoverageJsonFile
{
    public static bool TryWrite(string path, string json, [NotNullWhen(false)] out string? error)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
            error = null;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = e.Message;
            return false;
        }
    }
}
