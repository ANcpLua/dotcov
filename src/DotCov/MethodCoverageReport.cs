namespace DotCov;

/// <summary>
/// Result of <see cref="CoberturaParser.ParseMethods(Stream, long, FileIdentity)"/> and friends:
/// the raw per-method entries plus the diagnostics and source roots observed while reading them,
/// so nothing the parser noticed is lost on the method-level path.
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

    /// <summary>
    /// Files whose identity the parse could not resolve — the method-level twin of
    /// <see cref="CoverageReport.AmbiguousFiles"/>. A <see cref="CrapReport"/> analyzed from
    /// this report answers <see cref="GateOutcome.NoData"/> while any remain.
    /// </summary>
    public IReadOnlyList<AmbiguousFile> AmbiguousFiles { get; init; } = [];

    /// <summary>The exclusions applied to this report and the files they removed.</summary>
    public FileExclusion Exclusion { get; init; } = FileExclusion.None;

    // Retain identities removed by Exclude for metrics completeness accounting only.
    // They must never contribute lines, complexity, or unscored rows to the selected scope.
    internal IReadOnlyList<MethodCoverage> ExcludedMethods { get; init; } = [];

    /// <summary>
    /// Remove every method whose file matches <paramref name="patterns"/>, unless it matches
    /// <paramref name="keep"/> — exactly the substring-match semantics of
    /// <see cref="CoverageReport.Exclude(IEnumerable{string}, IEnumerable{string})"/>; the two
    /// share one predicate, so <c>--exclude-generated</c> and <c>--exclude-test-code</c> mean
    /// the same thing to <c>crap</c> as they do to <c>report</c> and <c>check</c>.
    /// </summary>
    public MethodCoverageReport Exclude(IEnumerable<string> patterns, IEnumerable<string> keep)
    {
        var rules = patterns.ToList();
        if (rules.Count is 0) return this;

        var keepRules = keep.ToList();
        var (kept, excluded) = ExclusionRules.Partition(Methods, static m => m.File, rules, keepRules);

        return new MethodCoverageReport(kept, Warnings, SourceRoots)
        {
            AmbiguousFiles = ExclusionRules.Remaining(AmbiguousFiles, excluded),
            Exclusion = Exclusion.Then(rules, keepRules, excluded),
            ExcludedMethods = ExcludedMethods.Concat(
                Methods.Where(m => excluded.Contains(m.File, StringComparer.Ordinal))).ToArray()
        };
    }
}
