namespace DotCov;

/// <summary>
/// A validated report-name pattern: <c>filename</c> (top level only) or <c>**/filename</c>
/// (recursive). The file name may carry wildcards but no directory separator. Any other shape
/// is rejected up front — a glob that quietly matches nothing flows into the gate as "nothing
/// was measured", the most invisible misconfiguration there is.
/// </summary>
public sealed class ReportPattern : IEquatable<ReportPattern>
{
    private const string RecursivePrefix = "**/";

    public const string DefaultText = "**/coverage.cobertura.xml";

    public static ReportPattern Default { get; } = Parse(DefaultText);

    private ReportPattern(string fileName, bool recursive)
    {
        FileName = fileName;
        Recursive = recursive;
    }

    /// <summary>The name portion, matched against file names only (wildcards allowed).</summary>
    public string FileName { get; }

    /// <summary>True for <c>**/filename</c>: descend into subdirectories, hidden ones included.</summary>
    public bool Recursive { get; }

    /// <summary>Parse and validate <paramref name="pattern"/>; throws <see cref="ArgumentException"/> for any other shape.</summary>
    public static ReportPattern Parse(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return TryParse(pattern, out var parsed)
            ? parsed
            : throw new ArgumentException(
                $"Unsupported pattern '{pattern}': only 'filename' and '**/filename' are supported.",
                nameof(pattern));
    }

    /// <summary>
    /// The pattern is split on the literal <c>**/</c> prefix, never on the host's directory
    /// separator, so a pattern means the same thing on every platform.
    /// </summary>
    public static bool TryParse(string? pattern, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ReportPattern? result)
    {
        result = null;
        if (pattern is null) return false;

        var recursive = pattern.StartsWith(RecursivePrefix, StringComparison.Ordinal);
        var name = recursive ? pattern[RecursivePrefix.Length..] : pattern;

        if (name.Length is 0 || name.Contains('/') || name.Contains('\\'))
            return false;

        result = new ReportPattern(name, recursive);
        return true;
    }

    public override string ToString() => Recursive ? RecursivePrefix + FileName : FileName;

    public bool Equals(ReportPattern? other) =>
        other is not null && Recursive == other.Recursive && string.Equals(FileName, other.FileName, StringComparison.Ordinal);

    public override bool Equals(object? obj) => Equals(obj as ReportPattern);

    public override int GetHashCode() => HashCode.Combine(FileName, Recursive);
}
