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
    /// machine-readable discriminator convention.
    /// </summary>
    public override string ToString()
    {
        var worst = WorstScore is { } w ? Invariant($"{w:F1}") : "n/a";
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
    /// Decide whether every scored method is at or below <paramref name="maxCrap"/>. A method
    /// exactly AT the threshold passes — the gate fires only strictly above, mirroring
    /// <c>check</c>'s at-threshold-passes semantics (see <see cref="CrapAnalysis.Exceeds"/>).
    /// Zero scored methods is <see cref="GateOutcome.NoData"/>, never a pass: a gate that
    /// cannot see cannot vouch for anything.
    /// </summary>
    public CrapGateResult Evaluate(double maxCrap)
    {
        if (Methods.Count is 0)
        {
            var reason = Unscored.Count > 0
                ? Invariant($"{Unscored.Count} methods have coverage but no usable complexity - supply --metrics <file> (dotnet msbuild /t:Metrics)")
                : "report carries no method-level data - nothing was measured per method";
            return new CrapGateResult(GateOutcome.NoData, maxCrap, 0, 0, null, reason);
        }

        var worst = Methods.Max(static m => m.Score);
        var above = Methods.Count(m => CrapAnalysis.Exceeds(m.Score, maxCrap));

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
    /// (GateResult owns that policy) is reused here, inverted for an upper bound.
    /// </summary>
    internal static bool Exceeds(double score, double maxCrap) =>
        score > maxCrap + GateResult.RateEpsilon;

    /// <summary>
    /// Score every method: fold compiler-synthesized methods into their source-method identity,
    /// resolve complexity (coverage-embedded first, metrics file second), and compute CRAP.
    /// Methods that cannot be scored and metrics members that match nothing are returned on the
    /// report's honesty channels — listed, never silently dropped.
    /// </summary>
    /// <param name="methods">Raw per-method coverage from <see cref="CoberturaParser.ParseMethods"/>.</param>
    /// <param name="metricsMembers">
    /// Optional complexity table from <see cref="CodeMetricsReader"/>. Used only for methods the
    /// coverage report carries no usable complexity for — the embedded value wins because it
    /// measured the exact assembly that was covered.
    /// </param>
    public static CrapReport Analyze(
        IReadOnlyList<MethodCoverage> methods,
        IReadOnlyList<CodeMetricsMember>? metricsMembers = null)
    {
        // ── 1. Fold raw coverage entries into logical source methods ─────────
        var byKey = new Dictionary<string, LogicalMethod>(StringComparer.Ordinal);
        var order = new List<LogicalMethod>();

        foreach (var mc in methods)
        {
            // Pure compiler infrastructure (display-class constructors, SetStateMachine, the
            // non-MoveNext surface of iterators) has no source-method identity to fold into —
            // and no lines a source method is missing.
            if (!MethodIdentity.TryNormalize(mc.ClassName, mc.MethodName, out var typeKey, out var methodKey, out var folded))
                continue;

            int? arity = folded ? null : MethodIdentity.SignatureArity(mc.Signature);

            // Direct entries key by the raw IL SIGNATURE, not arity: same-arity overloads
            // (Frob(Int32) vs Frob(String)) are distinct source methods, and collapsing them
            // would dilute an uncovered overload's CRAP with a covered sibling's lines.
            // Folded entries (state machines, lambdas) lost their origin's signature, so all
            // synthetic material for one origin name shares a single folded group.
            var key = folded
                ? Invariant($"{typeKey}|{methodKey}|<folded>")
                : Invariant($"{typeKey}|{methodKey}|{mc.Signature}");

            if (!byKey.TryGetValue(key, out var logical))
            {
                logical = new LogicalMethod(typeKey, methodKey, arity, folded, mc.File);
                byKey[key] = logical;
                order.Add(logical);
            }

            logical.MergeLines(mc.LineHits);
            if (mc.Complexity is { } c)
                logical.EmbeddedComplexity = logical.EmbeddedComplexity is { } e ? Math.Max(e, c) : c;
        }

        // ── 2. Fold synthetic groups into their origin method ────────────────
        // A lambda's lines belong inside its origin method's body: merging them makes cov(m)
        // span the same code Roslyn's complexity counts. Only an UNAMBIGUOUS origin (exactly one
        // same-named overload) absorbs; otherwise — including same-arity overload sets, which
        // step 1 keeps distinct — the folded group stands as its own row rather than guessing.
        var byName = new Dictionary<string, List<LogicalMethod>>(StringComparer.Ordinal);
        foreach (var logical in order)
        {
            if (logical.Folded) continue;
            var nameKey = $"{logical.TypeKey}|{logical.MethodKey}";
            if (!byName.TryGetValue(nameKey, out var list)) byName[nameKey] = list = [];
            list.Add(logical);
        }

        foreach (var logical in order)
        {
            if (!logical.Folded) continue;
            if (!byName.TryGetValue($"{logical.TypeKey}|{logical.MethodKey}", out var candidates) ||
                candidates.Count != 1)
                continue;

            var origin = candidates[0];
            origin.MergeLines(logical.Lines);
            if (logical.EmbeddedComplexity is { } c)
                origin.EmbeddedComplexity = origin.EmbeddedComplexity is { } e ? Math.Max(e, c) : c;
            logical.FoldedAway = true;
        }

        // ── 3. Resolve complexity and score ──────────────────────────────────
        var (metricsIndex, accessorFallback) = IndexMetrics(metricsMembers);
        var consumed = new HashSet<int>();
        var scored = new List<CrapMethod>();
        var unscored = new List<CrapUnscoredMethod>();

        foreach (var logical in order)
        {
            if (logical.FoldedAway) continue;
            var display = $"{logical.TypeKey}.{logical.MethodKey}";

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

            var hit = 0;
            var start = 0;
            foreach (var (line, hits) in logical.Lines)
            {
                if (hits > 0) hit++;
                if (start is 0 || line < start) start = line;
            }

            var coverage = (double)hit / logical.Lines.Count;
            scored.Add(new CrapMethod(display, logical.File, start, comp, coverage, Score(comp, coverage), source));
        }

        // ── 4. Unmatched metrics members (methods and accessors only) ────────
        // Property/field/event aggregates are complexity roll-ups with no executable coverage
        // counterpart of their own, so their absence is not a matching failure.
        var unmatched = new List<string>();
        if (metricsMembers is not null)
            for (var i = 0; i < metricsMembers.Count; i++)
                if (metricsMembers[i].Kind is CodeMetricsMemberKind.Method or CodeMetricsMemberKind.Accessor &&
                    !consumed.Contains(i))
                    unmatched.Add(metricsMembers[i].DisplayName);

        return new CrapReport(scored, unscored, unmatched);
    }

    /// <summary>
    /// Filter methods by file path, with exactly the substring-match semantics of
    /// <see cref="CoverageReport.Exclude(IEnumerable{string}, IEnumerable{string})"/> — the two
    /// share <see cref="ExclusionRules.Excluded"/>, so <c>--exclude-generated</c> means the same
    /// thing to <c>crap</c> as it does to <c>report</c> and <c>check</c>.
    /// </summary>
    public static IReadOnlyList<MethodCoverage> ExcludeFiles(
        IReadOnlyList<MethodCoverage> methods, IEnumerable<string> patterns, IEnumerable<string> keep)
    {
        var rules = patterns.ToList();
        if (rules.Count is 0) return methods;
        var keepRules = keep.ToList();

        return methods.Where(m => !ExclusionRules.Excluded(m.File, rules, keepRules)).ToList();
    }

    private sealed class LogicalMethod(string typeKey, string methodKey, int? arity, bool folded, string file)
    {
        public readonly string TypeKey = typeKey;
        public readonly string MethodKey = methodKey;
        public readonly int? Arity = arity;
        public readonly bool Folded = folded;
        public readonly string File = file;
        public readonly Dictionary<int, int> Lines = new();
        public int? EmbeddedComplexity;
        public bool FoldedAway;

        public void MergeLines(IReadOnlyDictionary<int, int> lines)
        {
            foreach (var (line, hits) in lines)
                Lines[line] = Lines.TryGetValue(line, out var existing) ? Math.Max(existing, hits) : hits;
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
                    // used only when no accessor entry matched.
                    accessorFallback.TryAdd($"{m.TypeName}|get_{m.MemberName}", i);
                    accessorFallback.TryAdd($"{m.TypeName}|set_{m.MemberName}", i);
                    break;
            }
        }

        return (index, accessorFallback);
    }

    private static (int? Complexity, CrapComplexitySource Source) ResolveComplexity(
        LogicalMethod logical,
        IReadOnlyList<CodeMetricsMember>? members,
        Dictionary<string, List<int>> metricsIndex,
        Dictionary<string, int> accessorFallback,
        HashSet<int> consumed)
    {
        // Embedded complexity wins: it measured the exact assembly that was covered.
        if (logical.EmbeddedComplexity is { } embedded)
            return (embedded, CrapComplexitySource.CoverageReport);

        var key = $"{logical.TypeKey}|{logical.MethodKey}";
        if (members is not null && metricsIndex.TryGetValue(key, out var candidates))
        {
            // Overload disambiguation by arity when both sides know it. When arity is unknown
            // (folded state machines/lambdas) or nothing matches it, take the MAX complexity
            // among the same-named candidates — the conservative direction for a gate — and
            // mark every candidate consulted as matched.
            var pool = candidates;
            if (logical.Arity is { } a)
            {
                var exact = candidates.Where(i => members[i].Arity == a).ToList();
                if (exact.Count > 0) pool = exact;
            }

            var best = 0;
            foreach (var i in pool)
            {
                consumed.Add(i);
                best = Math.Max(best, members[i].CyclomaticComplexity);
            }
            return (best, CrapComplexitySource.MetricsFile);
        }

        if (members is not null && accessorFallback.TryGetValue(key, out var propertyIndex))
        {
            consumed.Add(propertyIndex);
            return (members[propertyIndex].CyclomaticComplexity, CrapComplexitySource.MetricsFile);
        }

        return (null, CrapComplexitySource.MetricsFile);
    }
}
