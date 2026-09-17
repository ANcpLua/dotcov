namespace DotCov;

/// <summary>
/// Result of <see cref="CoberturaParser.ParseMethods(Stream, long)"/> and friends: the raw
/// per-method entries plus the diagnostics and source roots observed while reading them, so
/// nothing the parser noticed is lost on the method-level path.
/// </summary>
public sealed class MethodCoverageReport
{
    public static readonly MethodCoverageReport Empty = new([], [], []);

    public MethodCoverageReport(
        IReadOnlyList<MethodCoverage> methods,
        IReadOnlyList<CoverageWarning> warnings,
        IReadOnlyList<string> sourceRoots)
    {
        Methods = methods;
        Warnings = warnings;
        SourceRoots = sourceRoots;
    }

    /// <summary>One entry per distinct (file, class, method name, signature), in first-seen document order.</summary>
    public IReadOnlyList<MethodCoverage> Methods { get; }

    /// <summary>Every decoding anomaly raised while reading the inputs, in encounter order.</summary>
    public IReadOnlyList<CoverageWarning> Warnings { get; }

    /// <summary>
    /// Declared <c>&lt;source&gt;</c> roots across all inputs, deduplicated by normalized
    /// identity — the same rule <see cref="CoverageReport.SourceRoots"/> follows.
    /// </summary>
    public IReadOnlyList<string> SourceRoots { get; }
}
