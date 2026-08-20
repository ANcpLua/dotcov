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
        LoadReport(searchDirectory, "**/coverage.cobertura.xml", 50_000_000);

    /// <summary>
    /// <see cref="LoadReport(string)"/> with an explicit report-name pattern and per-file
    /// character cap — the NUKE-side twin of the CLI's <c>--pattern</c>/<c>--max-chars</c>
    /// (gcovr and coverage.py emit <c>coverage.xml</c>, which the default pattern never
    /// matches). A separate overload, not optional parameters on the existing signature:
    /// defaults are baked into compiled callers, so widening the published signature would
    /// be binary-breaking. An unsupported pattern (<see cref="CoberturaParser.ParseDirectory(string, string, long)"/>
    /// accepts only <c>filename</c> and <c>**/filename</c>) rethrows as a parameter error
    /// naming <c>Coverage Pattern</c>, consistent with the strict parsers below.
    /// </summary>
    public static CoverageReport LoadReport(string searchDirectory, string pattern, long maxChars)
    {
        if (!Directory.Exists(searchDirectory)) return CoverageReport.Empty;
        try
        {
            return CoberturaParser.ParseDirectory(searchDirectory, pattern, maxChars);
        }
        catch (ArgumentException ex)
        {
            throw new ArgumentException(
                $"Invalid Coverage Pattern: '{pattern}' (only 'filename' and '**/filename' are supported).", ex);
        }
    }

    /// <summary>
    /// Strict per-file character-cap parse mirroring the CLI's <c>--max-chars</c>: digits
    /// only (<see cref="NumberStyles.None"/> — a sign or separator is invalid, so negatives
    /// are rejected here rather than crashing <see cref="System.Xml.XmlReaderSettings"/>),
    /// and 0 means no cap (<see cref="System.Xml.XmlReaderSettings.MaxCharactersInDocument"/>
    /// semantics).
    /// </summary>
    public static long ParseMaxChars(string value, string parameterName) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new ArgumentException(
                $"Invalid {parameterName}: '{value}' (expected a non-negative integer; 0 = no cap).");

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
    /// Strict format parse returning the canonical format name — "md" canonicalizes to
    /// "markdown". Anything but table/json/markdown/md throws naming the parameter instead
    /// of silently falling back to table.
    /// </summary>
    public static string ParseFormat(string value, string parameterName) =>
        value switch
        {
            "table" or "json" or "markdown" => value,
            "md" => "markdown",
            _ => throw new ArgumentException(
                $"Invalid {parameterName}: '{value}' (expected 'table', 'json', 'markdown', or 'md').")
        };

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
