using System.Globalization;

namespace DotCov.Fallout;

internal enum CoverageFormat { Table, Json, Markdown }

/// <summary>
/// The validated form of <see cref="ICoverageReport"/>'s parameters. Every value is parsed
/// exactly once, at the build boundary, with the same grammar the CLI uses: invariant
/// numbers, digits-only character cap, strict <c>true</c>/<c>false</c>, the <c>md</c> alias,
/// and the <see cref="ReportPattern"/> shapes. Anything else fails naming the parameter.
/// </summary>
internal sealed record CoverageParameters(
    double MinLine,
    double MinBranch,
    CoverageFormat Format,
    bool ExcludeGenerated,
    ReportPattern Pattern,
    long MaxChars)
{
    public const string DefaultMinLine = "80";
    public const string DefaultMinBranch = "0";
    public const string DefaultFormat = "table";
    public const string DefaultExcludeGenerated = "false";
    public const string DefaultPattern = ReportPattern.DefaultText;
    public const string DefaultMaxChars = "50000000";

    public static CoverageParameters Parse(
        string minLine, string minBranch, string format, string excludeGenerated, string pattern, string maxChars) =>
        new(
            ParseThreshold(minLine, "Coverage MinLine"),
            ParseThreshold(minBranch, "Coverage MinBranch"),
            ParseFormat(format, "Coverage Format"),
            ParseFlag(excludeGenerated, "Coverage ExcludeGeneratedParam"),
            ParsePattern(pattern, "Coverage Pattern"),
            ParseMaxChars(maxChars, "Coverage MaxCharsParam"));

    /// <summary>Culture-invariant number; a comma decimal separator is garbage, not 80.5.</summary>
    public static double ParseThreshold(string value, string parameterName) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new ArgumentException($"Invalid {parameterName}: '{value}' (expected a number).");

    /// <summary>Digits only: a sign or separator is invalid, so negatives never reach the XML reader. 0 = no cap.</summary>
    public static long ParseMaxChars(string value, string parameterName) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw new ArgumentException(
                $"Invalid {parameterName}: '{value}' (expected a non-negative integer; 0 = no cap).");

    /// <summary>Strict boolean: truthy spellings like "1" or "yes" fail instead of silently reading as false.</summary>
    public static bool ParseFlag(string value, string parameterName) =>
        bool.TryParse(value, out var parsed)
            ? parsed
            : throw new ArgumentException($"Invalid {parameterName}: '{value}' (expected 'true' or 'false').");

    /// <summary>Case-sensitive format name; "md" is the one alias. An unknown format fails instead of falling back to table.</summary>
    public static CoverageFormat ParseFormat(string value, string parameterName) =>
        value switch
        {
            "table" => CoverageFormat.Table,
            "json" => CoverageFormat.Json,
            "markdown" or "md" => CoverageFormat.Markdown,
            _ => throw new ArgumentException(
                $"Invalid {parameterName}: '{value}' (expected 'table', 'json', 'markdown', or 'md').")
        };

    public static ReportPattern ParsePattern(string value, string parameterName) =>
        ReportPattern.TryParse(value, out var parsed)
            ? parsed
            : throw new ArgumentException(
                $"Invalid {parameterName}: '{value}' (only 'filename' and '**/filename' are supported).");
}
