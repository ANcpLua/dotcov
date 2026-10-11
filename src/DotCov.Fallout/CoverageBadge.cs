using System.Diagnostics.CodeAnalysis;
using DotCov.Formatters;

namespace DotCov.Fallout;

/// <summary>
/// Writes the evaluated gate as <see cref="BadgeFormatter.SvgFileName"/> and
/// <see cref="BadgeFormatter.JsonFileName"/>. Rendering stays in DotCov; this only creates the
/// directory and writes, reporting a failure to the caller, which decides what it costs.
/// </summary>
internal static class CoverageBadge
{
    public static bool TryWrite(string directory, GateResult gate, [NotNullWhen(false)] out string? error)
    {
        var svg = BadgeFormatter.Svg(gate);
        var json = BadgeFormatter.ShieldsJson(gate);
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, BadgeFormatter.SvgFileName), svg);
            File.WriteAllText(Path.Combine(directory, BadgeFormatter.JsonFileName), json);
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
