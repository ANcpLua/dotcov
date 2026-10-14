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
        var normalizedMetrics = metricsMembers?.Select(m => m with { File = MetricsFile(m.File, report.SourceRoots) }).ToArray();
        var (selectedMetrics, exclusion) = report.SelectMetrics(normalizedMetrics);
        var analysis = AnalyzeCore(report.Methods, selectedMetrics, report.ExcludedMethods);
        return new CrapReport(analysis.Methods, analysis.Unscored, analysis.UnmatchedMetricsMembers)
        {
            AmbiguousFiles = report.AmbiguousFiles,
            Exclusion = exclusion
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

        var excluded = FoldRawEntries(excludedMethods);
        FoldSyntheticGroups(excluded);
        metricsMembers = PrepareMetrics(metricsMembers, order.Concat(excluded));
        var metricSignatures = metricsMembers?.Select(static m => MethodSignatures.Metrics(m.DisplayName)).ToArray() ?? [];
        var qualifications = MethodSignatures.TypeQualifications(order.Concat(excluded).Select(static m => m.Signature),
            metricSignatures);
        foreach (var logical in order.Concat(excluded))
            logical.Signature = MethodSignatures.QualifyTypes(logical.Signature, qualifications);
        var nullableIdentities = new MethodSignatures.NullableIdentities(order.Concat(excluded).Select(m => m.Signature));
        var signatures = metricSignatures.Select(s => MethodSignatures.ResolveNullable(
            MethodSignatures.QualifyTypes(s, qualifications, fromMetrics: true), nullableIdentities)).ToArray();
        var consumed = new HashSet<int>();
        // Exclusions remove methods from the measurement scope, not from the metrics file.
        // Match their identities without scoring them, even when they have no line data.
        var metricsIndex = IndexMetrics(metricsMembers, signatures);
        // Source-bearing exclusions were removed by SelectMetrics. A retained member's
        // known source must not be consumed by an excluded same-name sibling.
        var excludedMetricsIndex = IndexMetrics(metricsMembers, signatures, onlyWithoutSource: true);
        // Different raw signatures/files can have the same available metrics identity.
        // Account for that identity once, even when every candidate is a zero-body declaration.
        foreach (var logical in excluded.Where(static l => !l.FoldedAway)
                     .DistinctBy(static l => (l.Name.TypeIdentity, MethodIdentity.MetricsMemberName(l.Name.Method), l.Arity, l.Signature)))
            MatchExcludedMetrics(logical, excludedMetricsIndex, consumed);

        var (scored, unscored) = ScoreMethods(order, metricsMembers, metricsIndex, consumed); // 3. resolve comp, score
        ReconcileConstructorPairs(order, metricsMembers, consumed);
        var unmatched = CollectUnmatched(metricsMembers, consumed);               // 4. honesty channel

        return new CrapReport(scored, unscored, unmatched);
    }

    // Roslyn displays static and parameterless instance constructors identically. Only a
    // complete, measured pair in the same type and file proves both indistinguishable rows.
    private static void ReconcileConstructorPairs(List<LogicalMethod> order,
        IReadOnlyList<CodeMetricsMember>? members, HashSet<int> consumed)
    {
        if (members is null) return;
        var pairs = order.Where(static method => !method.FoldedAway && method.Lines.Count > 0 &&
                method.Signature is "" && method.Name.Method is ".ctor" or ".cctor")
            .GroupBy(static method => method.Name.TypeIdentity)
            .Where(static group => group.Count() == 2 &&
                group.Select(method => method.Name.Method).Distinct().Count() == 2 &&
                group.Select(method => method.File).Distinct().Count() == 1)
            .ToDictionary(static group => group.Key, static group => group.First().File);
        foreach (var group in members.Select(static (member, index) => (Member: member, Index: index))
                     .Where(static entry => entry.Member.Kind is CodeMetricsMemberKind.Method &&
                         entry.Member.MemberName is ".ctor" && entry.Member.ExecutableLines is not 0 &&
                         entry.Member.Arity is null or 0)
                     .GroupBy(static entry => entry.Member.TypeIdentity ?? entry.Member.TypeName))
        {
            var candidates = group.ToArray();
            if (candidates.Length != 2 || candidates.Any(static entry =>
                    MethodSignatures.Metrics(entry.Member.DisplayName) is not "")) continue;
            if (!pairs.TryGetValue(group.Key, out var file) || !candidates.All(entry =>
                    string.IsNullOrWhiteSpace(entry.Member.File) || entry.Member.File is "UNKNOWN" ||
                    PathIdentity.NormalizeRoot(entry.Member.File) == file)) continue;
            foreach (var entry in candidates) consumed.Add(entry.Index);
        }
    }

    // Scoring may conservatively take the maximum complexity of several candidates.
    // Exclusions cannot use that fallback: consuming siblings would hide missing coverage.
    // Without source-file/signature identity, only a unique compatible member is proven.
    private static void MatchExcludedMetrics(
        LogicalMethod logical,
        MetricsIndex metricsIndex,
        HashSet<int> consumed)
    {
        var key = $"{logical.Name.TypeIdentity}|{MethodIdentity.MetricsMemberName(logical.Name.Method)}";
        var compatible = CompatibleMetrics(metricsIndex, key, logical.Arity, logical.Signature, logical.File);
        if (compatible.UniqueMember is { } index) consumed.Add(index);
    }

    private static IReadOnlyList<CodeMetricsMember>? PrepareMetrics(
        IReadOnlyList<CodeMetricsMember>? members, IEnumerable<LogicalMethod> methods)
    {
        if (members is null) return null;
        // Legacy callers supplied the generic-stripped TypeName. Recover arity only when
        // the coverage scope contains exactly one possible declaring-type identity.
        var types = methods.GroupBy(static m => m.TypeKey).ToDictionary(static g => g.Key,
            static g => g.Select(m => m.Name.TypeIdentity).Distinct().Take(2).ToArray());
        // Cyclomatic complexity is at least 1 for anything executable: a lower value measured
        // nothing and must not score 0. Only an explicit zero-body declaration keeps it, to stay
        // out of the completeness accounting.
        return members.Where(static m => m.CyclomaticComplexity >= 1 || m.ExecutableLines is 0).Select(m => m with
        {
            TypeIdentity = m.TypeIdentity ?? (types.TryGetValue(m.TypeName, out var identities) &&
                identities.Length == 1 ? identities[0] : null),
            File = MetricsFile(m.File, [])
        }).ToArray();
    }

    private static string? MetricsFile(string? file, IReadOnlyList<string> roots)
    {
        if (string.IsNullOrWhiteSpace(file) || file is "UNKNOWN") return file;
        var path = PathIdentity.NormalizeRoot(file);
        if (roots.Count == 1 && roots[0].Length > 0 && !path.StartsWith('/') &&
            !(path.Length >= 2 && path[1] == ':'))
            path = PathIdentity.NormalizeRoot($"{roots[0].TrimEnd('/')}/{path}");
        return path;
    }

    private static MetricsCandidates CompatibleMetrics(MetricsIndex index, string key, int? arity,
        string? signature, string file)
    {
        var scoped = index.ByFile.GetValueOrDefault(PathIdentity.NormalizeRoot(file));
        if (arity is null) return MergeCandidates(index.All.GetValueOrDefault(key),
            scoped?.All.GetValueOrDefault(key) ?? default);
        var compatible = MergeCandidates(
            MergeCandidates(index.ByArity.GetValueOrDefault((key, arity)),
                index.ByArity.GetValueOrDefault((key, null))),
            MergeCandidates(scoped?.ByArity.GetValueOrDefault((key, arity)) ?? default,
                scoped?.ByArity.GetValueOrDefault((key, null)) ?? default));
        if (signature is not null && compatible.ExecutableCount > 0 && !compatible.HasUnknownSignature)
        {
            var exact = MergeCandidates(index.BySignature.GetValueOrDefault((key, signature)),
                scoped?.BySignature.GetValueOrDefault((key, signature)) ?? default);
            return exact.ExecutableCount > 0 ? exact : exact with { SignatureMismatch = true };
        }
        return compatible;
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
                logical = new LogicalMethod(name, typeKey, arity, origin,
                    origin is null ? MethodSignatures.Coverage(mc.Signature) : null);
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
        MetricsIndex metricsIndex,
        HashSet<int> consumed)
    {
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

            var (complexity, source) = ResolveComplexity(logical, metricsIndex, consumed);
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

    private sealed class LogicalMethod(MethodName name, string typeKey, int? arity, SyntheticOrigin? origin, string? signature)
    {
        public readonly MethodName Name = name;
        public readonly string TypeKey = typeKey;
        public readonly int? Arity = arity;
        public string? Signature = signature;
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

    private readonly record struct MetricsCandidates(int? Complexity, int? UniqueMember, int ExecutableCount,
        bool HasUnknownSignature)
    {
        public bool SignatureMismatch { get; init; }
    }

    private sealed class MetricsIndex
    {
        public readonly Dictionary<string, MetricsCandidates> All = new(StringComparer.Ordinal);
        public readonly Dictionary<(string Key, int? Arity), MetricsCandidates> ByArity = new();
        public readonly Dictionary<(string Key, string Signature), MetricsCandidates> BySignature = new();
        public readonly Dictionary<string, (int Index, int Complexity)> AccessorFallback = new(StringComparer.Ordinal);
        public readonly Dictionary<int, string> PropertyFiles = new();
        public readonly Dictionary<string, MetricsIndex> ByFile = new(StringComparer.Ordinal);
    }

    private static MetricsCandidates MergeCandidates(MetricsCandidates left, MetricsCandidates right)
    {
        var complexity = left.Complexity is { } l && right.Complexity is { } r
            ? Math.Max(l, r) : left.Complexity ?? right.Complexity;
        var count = Math.Min(2, left.ExecutableCount + right.ExecutableCount);
        return new MetricsCandidates(complexity, count is 1 ? left.UniqueMember ?? right.UniqueMember : null, count,
            left.HasUnknownSignature || right.HasUnknownSignature);
    }

    private static void AddCandidates<TKey>(Dictionary<TKey, MetricsCandidates> index, TKey key,
        MetricsCandidates candidate) where TKey : notnull =>
        index[key] = MergeCandidates(index.GetValueOrDefault(key), candidate);

    private static MetricsIndex IndexMetrics(IReadOnlyList<CodeMetricsMember>? members, string?[] signatures,
        bool onlyWithoutSource = false)
    {
        var index = new MetricsIndex();
        if (members is null) return index;

        for (var i = 0; i < members.Count; i++)
        {
            var m = members[i];
            if (onlyWithoutSource && !string.IsNullOrWhiteSpace(m.File) && m.File is not "UNKNOWN") continue;
            var target = index;
            if (!string.IsNullOrWhiteSpace(m.File) && m.File is not "UNKNOWN")
            {
                if (!index.ByFile.TryGetValue(m.File, out target))
                    index.ByFile.Add(m.File, target = new MetricsIndex());
            }
            switch (m.Kind)
            {
                case CodeMetricsMemberKind.Method or CodeMetricsMemberKind.Accessor:
                    var key = $"{m.TypeIdentity ?? m.TypeName}|{m.MemberName}";
                    var signature = signatures[i];
                    // Retain an empty bucket for explicit zero-body accessors so an aggregate
                    // cannot restore the declaration's non-executable complexity as a fallback.
                    var candidate = m.ExecutableLines is 0
                        ? default : new MetricsCandidates(m.CyclomaticComplexity, i, 1, signature is null);
                    AddCandidates(target.All, key, candidate);
                    AddCandidates(target.ByArity, (key, m.Arity), candidate);
                    if (signature is not null) AddCandidates(target.BySignature, (key, signature), candidate);
                    // The metrics display omits static/instance information for extension
                    // blocks. An inferred receiver must not prove a match for a static member.
                    break;

                case CodeMetricsMemberKind.Property:
                    // Older Metrics versions emit no <Accessors> children; the property
                    // aggregate (sum over its accessors) is the honest-but-coarser fallback,
                    // used only when no accessor entry matched. An aggregate below 1 summed no
                    // declared accessor (a positional record property) and never gets here.
                    if (m.ExecutableLines is 0) break;
                    target.AccessorFallback.TryAdd($"{m.TypeIdentity ?? m.TypeName}|get_{m.MemberName}", (i, m.CyclomaticComplexity));
                    target.AccessorFallback.TryAdd($"{m.TypeIdentity ?? m.TypeName}|set_{m.MemberName}", (i, m.CyclomaticComplexity));
                    break;
            }
        }

        return index;
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
        MetricsIndex metricsIndex,
        HashSet<int> consumed)
    {
        var fromMetrics = MatchMetrics(logical, metricsIndex, consumed);

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
        MetricsIndex metricsIndex,
        HashSet<int> consumed)
    {
        var key = $"{logical.Name.TypeIdentity}|{MethodIdentity.MetricsMemberName(logical.Name.Method)}";
        var scoped = metricsIndex.ByFile.GetValueOrDefault(PathIdentity.NormalizeRoot(logical.File));
        if (metricsIndex.All.ContainsKey(key) || (scoped?.All.ContainsKey(key) ?? false))
        {
            // Keep the conservative complexity fallback when no arity matches, but never
            // treat a provably incompatible candidate as evidence of complete coverage.
            var compatible = CompatibleMetrics(metricsIndex, key, logical.Arity, logical.Signature, logical.File);
            if (compatible.SignatureMismatch) return null;
            if (compatible.UniqueMember is { } index && !consumed.Add(index)) return null;
            return compatible.Complexity ?? MergeCandidates(metricsIndex.All.GetValueOrDefault(key),
                scoped?.All.GetValueOrDefault(key) ?? default).Complexity;
        }

        var propertyFound = scoped is not null && scoped.AccessorFallback.TryGetValue(key, out _);
        var propertyIndex = propertyFound ? scoped! : metricsIndex;
        if (propertyIndex.AccessorFallback.TryGetValue(key, out var property))
        {
            // A property roll-up can serve both accessors, but not a different file's property.
            if (metricsIndex.PropertyFiles.TryGetValue(property.Index, out var file) && file != logical.File) return null;
            metricsIndex.PropertyFiles[property.Index] = logical.File;
            consumed.Add(property.Index);
            return property.Complexity;
        }

        return null;
    }
}
