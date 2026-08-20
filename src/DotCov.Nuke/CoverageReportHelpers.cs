using System.Globalization;

namespace DotCov.Nuke;

/// <summary>
/// Static helpers behind <see cref="ICoverageReport"/>. Extracted from the target body so the
/// parameter parsing, discovery, and step-summary policies are unit-testable without a NUKE
/// build host.
/// </summary>
public static class CoverageReportHelpers
{
    /// <summary>
    /// Discovers and merges coverage via <see cref="CoberturaParser.ParseDirectory"/> —
    /// the library's hardened path (deterministic ordinal file order). A missing directory
    /// behaves like an empty one: both yield the <see cref="CoverageReport.Empty"/> singleton,
    /// which lets callers distinguish "no files discovered" from a parsed-but-empty report.
    /// </summary>
    public static CoverageReport LoadReport(string searchDirectory) =>
        Directory.Exists(searchDirectory)
            ? CoberturaParser.ParseDirectory(searchDirectory)
            : CoverageReport.Empty;

    /// <summary>Culture-invariant numeric parse; throws naming the parameter on garbage.</summary>
    public static double ParseThreshold(string value, string parameterName) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new ArgumentException($"Invalid {parameterName}: '{value}' (expected a number).");

    /// <summary>
    /// Strict boolean parse. Anything but true/false — including truthy spellings like
    /// "1" or "yes" — throws instead of silently reading as false.
    /// </summary>
    public static bool ParseFlag(string value, string parameterName) =>
        bool.TryParse(value, out var parsed)
            ? parsed
            : throw new ArgumentException($"Invalid {parameterName}: '{value}' (expected 'true' or 'false').");

    /// <summary>
    /// Appends <paramref name="markdown"/> to the GitHub step summary at <paramref name="path"/>.
    /// Returns false — never throws — when the path is null/empty or cannot be written:
    /// a bad <c>GITHUB_STEP_SUMMARY</c> must not fail an otherwise green build.
    /// </summary>
    public static bool TryAppendGitHubStepSummary(string? path, string markdown)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            File.AppendAllText(path, markdown);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
