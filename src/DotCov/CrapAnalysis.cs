using static System.FormattableString;

namespace DotCov;

/// <summary>Where a scored method's cyclomatic complexity came from.</summary>
public enum CrapComplexitySource
{
    /// <summary>
    /// The coverage report itself (coverlet's per-<c>&lt;method&gt;</c> <c>complexity</c>
    /// attribute) — the zero-extra-file path. Measures each IL method separately: a lambda or
    /// state machine folded into its origin method contributes lines but its complexity merges
    /// by <c>Math.Max</c>, not by sum.
    /// </summary>
    CoverageReport,

    /// <summary>
    /// A Microsoft.CodeAnalysis.Metrics file (<c>--metrics</c>). Roslyn counts the whole source
    /// method body, including nested lambdas and local functions.
    /// </summary>
    MetricsFile,
}

/// <summary>One scored method: the CRAP inputs and the score they produce.</summary>
/// <param name="Method">Normalized display identity, e.g. <c>MyApp.Calculator.AddAsync</c>.</param>
/// <param name="File">File identity key (same normalization as <see cref="FileCoverage.Path"/>).</param>
/// <param name="StartLine">First covered-or-coverable line of the method (0 when unknown).</param>
/// <param name="Complexity">Cyclomatic complexity (comp).</param>
/// <param name="Coverage">Line-coverage ratio 0..1 (cov) — the basis-path approximation.</param>
/// <param name="Score">CRAP(m) = comp² · (1 − cov)³ + comp.</param>
/// <param name="ComplexitySource">Which source supplied comp.</param>
public readonly record struct CrapMethod(
    string Method,
    string File,
    int StartLine,
    int Complexity,
    double Coverage,
    double Score,
    CrapComplexitySource ComplexitySource);

/// <summary>A method that has coverage but could not be scored, and why. Listed, never dropped.</summary>
public readonly record struct CrapUnscoredMethod(string Method, string File, string Reason);

/// <summary>
/// The verdict of <see cref="CrapReport.Evaluate"/>. Same philosophy as <see cref="GateResult"/>:
/// outcome plus the numbers it was reached from, no exit code, no severity —
/// policy belongs to the caller.
/// </summary>
public readonly record struct CrapGateResult(
    GateOutcome Outcome,
    double MaxCrap,
    int ScoredMethods,
    int AboveThreshold,
    double? WorstScore,
    string Reason)
{
    /// <summary>True only for <see cref="GateOutcome.Pass"/>.</summary>
    public bool IsPass => Outcome is GateOutcome.Pass;

    /// <summary>
    /// One-line invariant-formatted summary, e.g.
    /// <c>FAIL: worst CRAP 30.0 (max 6) - 2 of 12 methods above threshold</c>. The first stderr
    /// token (<c>PASS:</c>/<c>FAIL:</c>/<c>NODATA:</c>) matches the <c>check</c> command's
    /// machine-readable discriminator convention. Like <see cref="GateResult.ToString"/>, a
    /// failing figure rounds toward the verdict (up, for this upper bound — see
    /// <see cref="CrapAnalysis.DisplayScore"/>), so a FAIL never prints a worst score that reads
    /// as at the threshold: 30.03 against 30 renders <c>30.1</c>, not
    /// <c>FAIL: worst CRAP 30.0 (max 30)</c>.
    /// </summary>
    public override string ToString()
    {
        var worst = WorstScore is { } w ? Invariant($"{CrapAnalysis.DisplayScore(w, MaxCrap):F1}") : "n/a";
        return Invariant($"{Outcome.ToString().ToUpperInvariant()}: worst CRAP {worst} (max {MaxCrap}) - {Reason}");
    }
}

/// <summary>
/// The CRAP gate's analysis result: scored methods, plus the two honesty channels — methods
/// with coverage but no usable complexity, and metrics members that matched no coverage method.
/// Both are listed rather than silently dropped, because a gate that quietly ignores what it
/// cannot see is how a red metric turns green without anyone deciding it should.
/// </summary>
public sealed class CrapReport
{
    internal CrapReport(
        IReadOnlyList<CrapMethod> methods,
        IReadOnlyList<CrapUnscoredMethod> unscored,
        IReadOnlyList<string> unmatchedMetricsMembers)
    {
        Methods = methods;
        Unscored = unscored;
        UnmatchedMetricsMembers = unmatchedMetricsMembers;
    }

    /// <summary>
    /// Files whose identity the parse could not resolve, carried from the
    /// <see cref="MethodCoverageReport"/> this report was analyzed from. While any remain,
    /// <see cref="Evaluate"/> answers <see cref="GateOutcome.NoData"/>.
    /// </summary>
    public IReadOnlyList<AmbiguousFile> AmbiguousFiles { get; internal init; } = [];

    /// <summary>The exclusions applied to the analyzed methods and the files they removed.</summary>
    public FileExclusion Exclusion { get; internal init; } = FileExclusion.None;

    /// <summary>Scored methods in document order; formatters apply worst-first ordering.</summary>
    public IReadOnlyList<CrapMethod> Methods { get; }

    /// <summary>Methods with coverage data but no usable complexity source — never gated, always listed.</summary>
    public IReadOnlyList<CrapUnscoredMethod> Unscored { get; }

    /// <summary>
    /// Metrics-file members (methods and accessors) that matched no coverage method — a
    /// normalization gap, code compiled out of the measured assembly, or an uninstrumented
    /// member. Empty when no metrics file was supplied.
    /// </summary>
    public IReadOnlyList<string> UnmatchedMetricsMembers { get; }

    /// <summary>
    /// Decide whether every method is at or below <paramref name="maxCrap"/>. A method exactly AT
    /// the threshold passes — the gate fires only strictly above, mirroring <c>check</c>'s
    /// at-threshold-passes semantics (see <see cref="CrapAnalysis.Exceeds"/>). The verdict needs
    /// COMPLETE scoring: a single <see cref="Unscored"/> method or
    /// <see cref="UnmatchedMetricsMembers"/> entry makes it
    /// <see cref="GateOutcome.NoData"/>, because the method the gate cannot score may be the
    /// one above the limit; zero scored methods is NoData too, and so is a report with
    /// <see cref="AmbiguousFiles"/>. A gate that cannot see everything cannot vouch for anything.
    /// </summary>
    public CrapGateResult Evaluate(double maxCrap)
    {
        double? worst = Methods.Count is 0 ? null : Methods.Max(static m => m.Score);
        var above = Methods.Count(m => CrapAnalysis.Exceeds(m.Score, maxCrap));

        // Nothing can exceed an infinite threshold, so the gate cannot fail — reported as
        // Disabled, like check's 0/0, never as a pass.
        if (double.IsPositiveInfinity(maxCrap))
            return new CrapGateResult(GateOutcome.Disabled, maxCrap, Methods.Count, 0, worst,
                "no finite threshold - this gate cannot fail");

        if (AmbiguousFiles.Count > 0)
            return new CrapGateResult(GateOutcome.NoData, maxCrap, Methods.Count, above, worst,
                AmbiguousFile.GateReason(AmbiguousFiles));

        if (Unscored.Count > 0)
            return new CrapGateResult(GateOutcome.NoData, maxCrap, Methods.Count, above, worst,
                Invariant($"{Unscored.Count} of {Methods.Count + Unscored.Count} methods could not be scored - ") +
                "supply --metrics <file> (dotnet msbuild /t:Metrics); a verdict needs every method scored");

        if (UnmatchedMetricsMembers.Count > 0)
            return new CrapGateResult(GateOutcome.NoData, maxCrap, Methods.Count, above, worst,
                Invariant($"{UnmatchedMetricsMembers.Count} metrics members without coverage data - ") +
                "a verdict needs every method scored");

        if (Methods.Count is 0)
            return new CrapGateResult(GateOutcome.NoData, maxCrap, 0, 0, null,
                "report carries no method-level data - nothing was measured per method");

        return above is 0
            ? new CrapGateResult(GateOutcome.Pass, maxCrap, Methods.Count, 0, worst,
                Invariant($"all {Methods.Count} methods at or below threshold"))
            : new CrapGateResult(GateOutcome.Fail, maxCrap, Methods.Count, above, worst,
                Invariant($"{above} of {Methods.Count} methods above threshold"));
    }
}

/// <summary>
/// CRAP — Change Risk Anti-Patterns (Savoia &amp; Cunningham):
/// <c>CRAP(m) = comp(m)² · (1 − cov(m))³ + comp(m)</c>, where comp is cyclomatic complexity and
/// cov approximates basis-path coverage by the method's line-coverage ratio (0..1).
/// Fully covered code scores its complexity; fully uncovered code scores comp² + comp.
/// </summary>
public static class CrapAnalysis
{
    /// <summary>The CRAP formula. <c>Score(5, 0) = 30</c>, <c>Score(5, 1) = 5</c>, <c>Score(1, 0) = 2</c>.</summary>
    public static double Score(int complexity, double coverage)
    {
        var miss = 1 - coverage;
        return (double)complexity * complexity * miss * miss * miss + complexity;
    }

    /// <summary>
    /// The one threshold comparison, used by <see cref="CrapReport.Evaluate"/> AND the
    /// formatters' offender highlighting so verdict and display can never drift. A score
    /// exactly at the threshold does NOT exceed it. The epsilon absorbs binary floating-point
    /// noise in the score so an exactly-met threshold passes — the same tolerance policy as
    /// <see cref="GateResult.MeetsThreshold"/>, whose <see cref="GateResult.RateEpsilon"/>
    /// (GateResult owns that policy) is reused here, inverted for an upper bound. Phrased as
    /// "not at or below" so a NaN threshold fails closed, as it does for the line/branch gate.
    /// </summary>
    internal static bool Exceeds(double score, double maxCrap) =>
        !(score <= maxCrap + GateResult.RateEpsilon);

    /// <summary>
    /// A score for one-decimal display, rounded toward its verdict: an offender rounds UP, so it
    /// never prints as a value that reads at or below <paramref name="maxCrap"/> (30.03 against 30
    /// renders <c>30.1</c>, not <c>30.0</c>); any other score is returned unchanged for ordinary
    /// rounding. The epsilon keeps float noise just above a tenth (42.000000000000007) from
    /// rounding an extra tenth up. The upper-bound mirror of <see cref="GateResult"/>'s flooring.
    /// </summary>
    internal static double DisplayScore(double score, double maxCrap) =>
        Exceeds(score, maxCrap) ? Math.Ceiling(score * 10 - GateResult.RateEpsilon) / 10 : score;

    /// <summary>
    /// Score every method of a parsed report: <see cref="Analyze(IReadOnlyList{MethodCoverage}, IReadOnlyList{CodeMetricsMember})"/>
    /// over its methods, with the report's unresolved file identities and exclusions carried
    /// onto the result, where <see cref="CrapReport.Evaluate"/> and the formatters read them.
    /// </summary>
    public static CrapReport Analyze(MethodCoverageReport report, IReadOnlyList<CodeMetricsMember>? metricsMembers = null)
    {
        ArgumentNullException.ThrowIfNull(report);
        var analysis = AnalyzeCore(report.Methods, metricsMembers, report.ExcludedMethods);
        return new CrapReport(analysis.Methods, analysis.Unscored, analysis.UnmatchedMetricsMembers)
        {
            AmbiguousFiles = report.AmbiguousFiles,
            Exclusion = report.Exclusion
        };
    }

    /// <summary>
    /// Score every method: fold compiler-synthesized methods into their source-method identity,
    /// resolve complexity (coverage-embedded first, metrics file second), and compute CRAP.
    /// Methods that cannot be scored and metrics members that match nothing are returned on the
    /// report's honesty channels — listed, never silently dropped.
    /// </summary>
    /// <param name="methods">Raw per-method coverage from <see cref="CoberturaParser.ParseMethods(Stream, long, FileIdentity)"/>.</param>
    /// <param name="metricsMembers">
    /// Optional complexity table from <see cref="CodeMetricsReader"/>. Used only for methods the
    /// coverage report carries no usable complexity for — the embedded value wins because it
    /// measured the exact assembly that was covered.
    /// </param>
    public static CrapReport Analyze(
        IReadOnlyList<MethodCoverage> methods,
        IReadOnlyList<CodeMetricsMember>? metricsMembers = null) =>
        AnalyzeCore(methods, metricsMembers, []);

    private static CrapReport AnalyzeCore(
        IReadOnlyList<MethodCoverage> methods,
        IReadOnlyList<CodeMetricsMember>? metricsMembers,
        IReadOnlyList<MethodCoverage> excludedMethods)
    {
        var order = FoldRawEntries(methods);            // 1. raw coverage → logical source methods
        FoldSyntheticGroups(order);                     // 2. state machines/lambdas → their origin

        var consumed = new HashSet<int>();
        // Exclusions remove methods from the measurement scope, not from the metrics file.
        // Match their identities without scoring them, even when they have no line data.
        var (metricsIndex, _) = IndexMetrics(metricsMembers);
        var excluded = FoldRawEntries(excludedMethods);
        FoldSyntheticGroups(excluded);
        foreach (var logical in excluded.Where(static l => !l.FoldedAway))
            MatchExcludedMetrics(logical, metricsMembers, metricsIndex, consumed);

        var (scored, unscored) = ScoreMethods(order, metricsMembers, consumed);   // 3. resolve comp, score
        var unmatched = CollectUnmatched(metricsMembers, consumed);               // 4. honesty channel

        return new CrapReport(scored, unscored, unmatched);
    }

    // Scoring may conservatively take the maximum complexity of several candidates.
    // Exclusions cannot use that fallback: consuming siblings would hide missing coverage.
    // Without source-file/signature identity, only a unique compatible member is proven.
    private static void MatchExcludedMetrics(
        LogicalMethod logical,
        IReadOnlyList<CodeMetricsMember>? members,
        Dictionary<string, List<int>> metricsIndex,
        HashSet<int> consumed)
    {
        if (members is null) return;
        var key = $"{logical.TypeKey}|{MethodIdentity.MetricsMemberName(logical.Name.Method)}";
        if (!metricsIndex.TryGetValue(key, out var candidates)) return;

        var compatible = candidates.Where(i => HasCompatibleArity(logical, members[i]));
        ConsumeUniqueExecutableMetrics(compatible, members, consumed);
    }

    private static bool HasCompatibleArity(LogicalMethod logical, CodeMetricsMember member)
    {
        if (logical.Arity is not { } coverageArity || member.Arity is not { } metricsArity) return true;
        return coverageArity == metricsArity ||
            (coverageArity == metricsArity + 1 && CodeMetricsReader.IsExtensionBlockMember(member.DisplayName));
    }

    private static void ConsumeUniqueExecutableMetrics(
        IEnumerable<int> candidates, IReadOnlyList<CodeMetricsMember> members, HashSet<int> consumed)
    {
        var executable = candidates.Where(i => members[i].ExecutableLines is not 0).Take(2).ToList();
        if (executable.Count is 1) consumed.Add(executable[0]);
    }

    /// <summary>
    /// Step 1: fold raw coverage entries into logical source methods, per FILE: the same type
    /// and method name in two files — two assemblies' <c>Ns.T.M(Int32)</c>, or two parts of a
    /// partial class — are two rows, never one averaged row. Direct entries key by the raw IL
    /// SIGNATURE, not arity: same-arity overloads (<c>Frob(Int32)</c> vs <c>Frob(String)</c>)
    /// are distinct source methods, and collapsing them would dilute an uncovered overload's
    /// CRAP with a covered sibling's lines. Folded entries (state machines, lambdas) lost their
    /// origin's signature, so they key by the origin's method ordinal instead: everything
    /// synthesized for one source method is one group, and the groups of two overloads stay
    /// apart. Types key by their arity-preserving identity for the same reason: <c>Result</c>
    /// and <c>Result`1</c> display alike but are different types.
    /// </summary>
    private static List<LogicalMethod> FoldRawEntries(IReadOnlyList<MethodCoverage> methods)
    {
        var byKey = new Dictionary<(MethodName Name, string Discriminator), LogicalMethod>();
        var order = new List<LogicalMethod>();

        foreach (var mc in methods)
        {
            // Pure compiler infrastructure (display-class constructors, SetStateMachine, the
            // non-MoveNext surface of iterators) has no source-method identity to fold into —
            // and no lines a source method is missing.
            if (!MethodIdentity.TryNormalize(mc.ClassName, mc.MethodName,
                    out var typeKey, out var typeIdentity, out var methodKey, out var origin))
                continue;

            var name = new MethodName(mc.File, typeIdentity, methodKey);
            var key = (name, origin is { } o ? $"<folded:{o.Ordinal}>" : mc.Signature);

            if (!byKey.TryGetValue(key, out var logical))
            {
                int? arity = origin is null ? MethodIdentity.SignatureArity(mc.Signature) : null;
                logical = new LogicalMethod(name, typeKey, arity, origin);
                byKey[key] = logical;
                order.Add(logical);
            }

            // Any own state-machine body makes this the origin's body, even if a lambda
            // created the group first. Never downgrade it when later entries are lambdas.
            if (origin is { OwnBody: true }) logical.Origin = origin;
            logical.MergeLines(mc.LineHits);
            logical.MergeEmbeddedComplexity(mc.Complexity);
        }

        return order;
    }

    /// <summary>
    /// Step 2: fold synthetic groups into their origin method. A lambda's lines belong inside
    /// its origin method's body: merging them makes cov(m) span the same code Roslyn's
    /// complexity counts. A group attaches only when its origin is established — exactly one
    /// same-named method in the same file and type, and exactly one synthetic origin of that
    /// name — and never on name alone: a method's own async/iterator body
    /// (<see cref="SyntheticOrigin.OwnBody"/>) does not attach to an entry that has lines of its
    /// own, because that entry is a different, synchronous overload. Everything else stands as
    /// its own scored row rather than guessing.
    /// </summary>
    private static void FoldSyntheticGroups(List<LogicalMethod> order)
    {
        var direct = order.Where(static l => !l.Folded).ToLookup(static l => l.Name);

        foreach (var synthetic in order.Where(static l => l.Folded).GroupBy(static l => l.Name))
        {
            var candidates = direct[synthetic.Key].ToList();
            if (candidates.Count != 1 || synthetic.Count() != 1) continue;

            var (origin, group) = (candidates[0], synthetic.First());
            if (group.Origin!.Value.OwnBody && origin.Lines.Count > 0) continue;

            origin.MergeLines(group.Lines);
            origin.MergeEmbeddedComplexity(group.EmbeddedComplexity);
            group.FoldedAway = true;
        }
    }

    /// <summary>Step 3: resolve each logical method's complexity and compute its CRAP score.</summary>
    private static (List<CrapMethod> Scored, List<CrapUnscoredMethod> Unscored) ScoreMethods(
        List<LogicalMethod> order,
        IReadOnlyList<CodeMetricsMember>? metricsMembers,
        HashSet<int> consumed)
    {
        var (metricsIndex, accessorFallback) = IndexMetrics(metricsMembers);
        var scored = new List<CrapMethod>();
        var unscored = new List<CrapUnscoredMethod>();

        foreach (var logical in order)
        {
            if (logical.FoldedAway) continue;
            var display = $"{logical.TypeKey}.{logical.Name.Method}";

            if (logical.Lines.Count is 0)
            {
                unscored.Add(new CrapUnscoredMethod(display, logical.File,
                    "method carries no line data - coverage is unmeasurable"));
                continue;
            }

            var (complexity, source) = ResolveComplexity(logical, metricsMembers, metricsIndex, accessorFallback, consumed);
            if (complexity is not { } comp)
            {
                unscored.Add(new CrapUnscoredMethod(display, logical.File,
                    metricsMembers is null
                        ? "no complexity in the coverage report - supply --metrics <file>"
                        : "no matching member in the metrics file"));
                continue;
            }

            var (coverage, start) = logical.LineStats();
            scored.Add(new CrapMethod(display, logical.File, start, comp, coverage, Score(comp, coverage), source));
        }

        return (scored, unscored);
    }

    /// <summary>
    /// Step 4: metrics members (methods and accessors only) that matched no coverage method.
    /// Property/field/event aggregates are complexity roll-ups with no executable coverage
    /// counterpart of their own, so their absence is not a matching failure.
    /// </summary>
    private static List<string> CollectUnmatched(IReadOnlyList<CodeMetricsMember>? metricsMembers, HashSet<int> consumed)
    {
        var unmatched = new List<string>();
        if (metricsMembers is not null)
            for (var i = 0; i < metricsMembers.Count; i++)
                if (metricsMembers[i].Kind is CodeMetricsMemberKind.Method or CodeMetricsMemberKind.Accessor &&
                    metricsMembers[i].ExecutableLines is not 0 &&
                    !consumed.Contains(i))
                    unmatched.Add(metricsMembers[i].DisplayName);
        return unmatched;
    }

    /// <summary>A logical method's name: file identity key, arity-preserving type identity, method name.</summary>
    private readonly record struct MethodName(string File, string TypeIdentity, string Method);

    private sealed class LogicalMethod(MethodName name, string typeKey, int? arity, SyntheticOrigin? origin)
    {
        public readonly MethodName Name = name;
        public readonly string TypeKey = typeKey;
        public readonly int? Arity = arity;
        /// <summary>Set for compiler-synthesized material; null for a method the source names directly.</summary>
        public SyntheticOrigin? Origin = origin;
        public bool Folded => Origin is not null;
        public string File => Name.File;
        public readonly Dictionary<int, int> Lines = new();
        public int? EmbeddedComplexity;
        public bool FoldedAway;

        public void MergeLines(IReadOnlyDictionary<int, int> lines)
        {
            foreach (var (line, hits) in lines)
                Lines[line] = Lines.TryGetValue(line, out var existing) ? Math.Max(existing, hits) : hits;
        }

        /// <summary>
        /// Merge by <see cref="Math.Max"/>, never by sum: a lambda or state machine folded into
        /// its origin measures overlapping IL, and the largest per-IL-method value is the honest
        /// whole-method bound.
        /// </summary>
        public void MergeEmbeddedComplexity(int? complexity)
        {
            if (complexity is { } c)
                EmbeddedComplexity = EmbeddedComplexity is { } e ? Math.Max(e, c) : c;
        }

        /// <summary>Coverage ratio (covered ÷ coverable lines) and first known line (0 when none).</summary>
        public (double Coverage, int StartLine) LineStats()
        {
            var hit = 0;
            var start = 0;
            foreach (var (line, hits) in Lines)
            {
                if (hits > 0) hit++;
                if (start is 0 || line < start) start = line;
            }
            return ((double)hit / Lines.Count, start);
        }
    }

    private static (Dictionary<string, List<int>> Index, Dictionary<string, int> AccessorFallback) IndexMetrics(
        IReadOnlyList<CodeMetricsMember>? members)
    {
        var index = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var accessorFallback = new Dictionary<string, int>(StringComparer.Ordinal);
        if (members is null) return (index, accessorFallback);

        for (var i = 0; i < members.Count; i++)
        {
            var m = members[i];
            switch (m.Kind)
            {
                case CodeMetricsMemberKind.Method or CodeMetricsMemberKind.Accessor:
                    var key = $"{m.TypeName}|{m.MemberName}";
                    if (!index.TryGetValue(key, out var list)) index[key] = list = [];
                    list.Add(i);
                    break;

                case CodeMetricsMemberKind.Property:
                    // Older Metrics versions emit no <Accessors> children; the property
                    // aggregate (sum over its accessors) is the honest-but-coarser fallback,
                    // used only when no accessor entry matched. An aggregate below 1 summed no
                    // declared accessor (a positional record property) — nothing was measured.
                    if (m.CyclomaticComplexity < 1) break;
                    accessorFallback.TryAdd($"{m.TypeName}|get_{m.MemberName}", i);
                    accessorFallback.TryAdd($"{m.TypeName}|set_{m.MemberName}", i);
                    break;
            }
        }

        return (index, accessorFallback);
    }

    /// <summary>
    /// Two separate decisions. First the metrics table is consulted and a unique executable
    /// counterpart is marked matched, whether or not its value ends up being used.
    /// Then the value is chosen: embedded complexity wins because it
    /// measured the exact assembly that was covered; the metrics value fills in only when the
    /// report carries none.
    /// </summary>
    private static (int? Complexity, CrapComplexitySource Source) ResolveComplexity(
        LogicalMethod logical,
        IReadOnlyList<CodeMetricsMember>? members,
        Dictionary<string, List<int>> metricsIndex,
        Dictionary<string, int> accessorFallback,
        HashSet<int> consumed)
    {
        var fromMetrics = MatchMetrics(logical, members, metricsIndex, accessorFallback, consumed);

        if (logical.EmbeddedComplexity is { } embedded)
            return (embedded, CrapComplexitySource.CoverageReport);

        return (fromMetrics, CrapComplexitySource.MetricsFile);
    }

    /// <summary>
    /// The metrics-file complexity for <paramref name="logical"/>, marking only a unique
    /// executable candidate as matched; null when the table has no counterpart.
    /// </summary>
    private static int? MatchMetrics(
        LogicalMethod logical,
        IReadOnlyList<CodeMetricsMember>? members,
        Dictionary<string, List<int>> metricsIndex,
        Dictionary<string, int> accessorFallback,
        HashSet<int> consumed)
    {
        if (members is null) return null;

        var key = $"{logical.TypeKey}|{MethodIdentity.MetricsMemberName(logical.Name.Method)}";
        if (metricsIndex.TryGetValue(key, out var candidates))
        {
            // Keep the conservative complexity fallback when no arity matches, but never
            // treat a provably incompatible candidate as evidence of complete coverage.
            var compatible = candidates.Where(i => HasCompatibleArity(logical, members[i])).ToList();
            var pool = compatible.Count > 0 ? compatible : candidates;
            ConsumeUniqueExecutableMetrics(compatible, members, consumed);
            var best = 0;
            foreach (var i in pool)
            {
                best = Math.Max(best, members[i].CyclomaticComplexity);
            }
            return best;
        }

        if (accessorFallback.TryGetValue(key, out var propertyIndex))
        {
            consumed.Add(propertyIndex);
            return members[propertyIndex].CyclomaticComplexity;
        }

        return null;
    }
}
