using System.Globalization;

namespace DotCov.Fallout;

internal enum CoverageFormat { Table, Json, Markdown }

/// <summary>
/// The validated form of <see cref="ICoverageReport"/>'s parameters. Every value is parsed
/// exactly once, at the build boundary, with the same grammar the CLI uses: percentages from 0
/// to 100 with no default threshold, digits-only character cap, strict <c>true</c>/<c>false</c>,
/// the <c>md</c> alias, the <see cref="ReportPattern"/> shapes, and one file-identity mode.
/// Anything else fails naming the parameter.
/// </summary>
internal sealed record CoverageParameters(
    double MinLine,
    double MinBranch,
    CoverageFormat Format,
    bool ExcludeGenerated,
    bool ExcludeTestCode,
    bool ExcludeExternal,
    ReportPattern Pattern,
    long MaxChars,
    FileIdentity Identity)
{
    public const string DefaultFormat = "table";
    public const string DefaultExcludeGenerated = "false";
    public const string DefaultExcludeTestCode = "false";
    public const string DefaultExcludeExternal = "false";
    public const string DefaultPattern = ReportPattern.DefaultText;
    public const string DefaultMaxChars = "50000000";
    public const string DefaultBadgeDirectory = "";
    public const string DefaultJsonPath = "";
    public const string DefaultProbeSources = "false";

    public static CoverageParameters Parse(
        string? minLine, string? minBranch, string format, string excludeGenerated, string excludeTestCode,
        string excludeExternal, string pattern, string maxChars, string probeSources, string? sourceRoot)
    {
        // No default threshold: a gate nobody set a threshold for would pass on a number nobody chose.
        if (minLine is null && minBranch is null)
            throw new ArgumentException(
                "Missing Coverage MinLine and Coverage MinBranch: the coverage gate has no default threshold. " +
                "Set --coverage-min-line, --coverage-min-branch, or both (0 to 100; 0 disables a dimension).");

        return new CoverageParameters(
            minLine is null ? 0 : ParseThreshold(minLine, "Coverage MinLine"),
            minBranch is null ? 0 : ParseThreshold(minBranch, "Coverage MinBranch"),
            ParseFormat(format, "Coverage Format"),
            ParseFlag(excludeGenerated, "Coverage ExcludeGeneratedParam"),
            ParseFlag(excludeTestCode, "Coverage ExcludeTestCodeParam"),
            ParseFlag(excludeExternal, "Coverage ExcludeExternalParam"),
            ParsePattern(pattern, "Coverage Pattern"),
            ParseMaxChars(maxChars, "Coverage MaxCharsParam"),
            ParseIdentity(ParseFlag(probeSources, "Coverage ProbeSourcesParam"), sourceRoot));
    }

    /// <summary>The exclusion patterns the three flags select; empty when none is set.</summary>
    public IReadOnlyList<string> ExclusionPatterns =>
    [
        .. ExcludeGenerated ? ExclusionRules.WellKnown : [],
        .. ExcludeTestCode ? ExclusionRules.TestCode : [],
        .. ExcludeExternal ? ExclusionRules.External : [],
    ];

    /// <summary>
    /// A finite, culture-invariant number from 0 to 100; a comma decimal separator is garbage,
    /// not 80.5. NaN is rejected as the CLI rejects it: every comparison against NaN is false,
    /// so it gates nothing; above 100 nothing could pass, and below 0 nothing could fail.
    /// </summary>
    public static double ParseThreshold(string value, string parameterName) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && parsed is >= 0 and <= 100
            ? parsed
            : throw new ArgumentException($"Invalid {parameterName}: '{value}' (expected a number from 0 to 100).");

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

    /// <summary>
    /// The CLI's three file-identity modes: lexical by default, disk probing with
    /// ProbeSourcesParam, one declared root with SourceRoot. An empty SourceRoot is unset, like
    /// an empty BadgeDirectory; setting both is a contradiction, not a precedence rule.
    /// </summary>
    public static FileIdentity ParseIdentity(bool probeSources, string? sourceRoot)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot))
            return probeSources ? FileIdentity.ProbeSources : FileIdentity.Lexical;

        return probeSources
            ? throw new ArgumentException(
                "Coverage ProbeSourcesParam and Coverage SourceRoot are alternatives: set one of them.")
            : FileIdentity.FromSourceRoot(sourceRoot);
    }
}
