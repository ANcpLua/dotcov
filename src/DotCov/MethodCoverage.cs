using System.Collections.ObjectModel;

namespace DotCov;

/// <summary>
/// Per-method coverage detail from one Cobertura <c>&lt;method&gt;</c> entry, produced by the
/// opt-in <see cref="CoberturaParser.ParseMethods"/> family. Deliberately RAW: names are the
/// emitter's IL-level names (<c>ClassName</c> may be a compiler-synthesized state-machine or
/// display class like <c>Ns.Type/&lt;M&gt;d__3</c>, <c>MethodName</c> may be <c>MoveNext</c> or a
/// mangled lambda), and no folding or normalization is applied here — that interpretation layer
/// lives in <see cref="CrapAnalysis"/>, so this type can never silently lose an entry.
/// </summary>
/// <param name="ClassName">The Cobertura <c>&lt;class name&gt;</c> the method was emitted under.</param>
/// <param name="MethodName">The raw <c>&lt;method name&gt;</c> attribute.</param>
/// <param name="Signature">The raw <c>&lt;method signature&gt;</c> attribute (IL parameter list).</param>
/// <param name="File">
/// The file identity key, normalized exactly like <see cref="FileCoverage.Path"/> (separators
/// flipped, <c>&lt;source&gt;</c> root applied, drive letter uppercased) so method entries join
/// against the class-level report on the same key.
/// </param>
/// <param name="StartLine">Lowest line number the method covers; 0 when the method carries no lines.</param>
/// <param name="EndLine">Highest line number the method covers; 0 when the method carries no lines.</param>
/// <param name="LinesHit">Lines with at least one hit.</param>
/// <param name="LinesTotal">Lines the method carries.</param>
/// <param name="Complexity">
/// The emitter's cyclomatic complexity for this method, when usable. Coverlet emits a real
/// per-method count; gcovr/grcov/cover2cover emit a meaningless <c>0</c>/<c>0.0</c> and the
/// original Cobertura omits the attribute — both surface as <c>null</c> (cyclomatic complexity
/// is ≥ 1 by construction, so a value below 1 is "not measured", never a measurement).
/// </param>
public readonly record struct MethodCoverage(
    string ClassName,
    string MethodName,
    string Signature,
    string File,
    int StartLine,
    int EndLine,
    int LinesHit,
    int LinesTotal,
    int? Complexity)
{
    // Same default-instance safety net as FileCoverage: a default(MethodCoverage) never runs
    // property initializers, so the accessor null-coalesces to a shared empty singleton.
    private static readonly IReadOnlyDictionary<int, int> NoHits =
        ReadOnlyDictionary<int, int>.Empty;

    /// <summary>
    /// Ratio of hit lines, or <c>null</c> when the method carries no line data. Null is not zero:
    /// it means the question is unanswerable — same contract as <see cref="FileCoverage.LineRate"/>.
    /// </summary>
    public double? LineRate => LinesTotal is 0 ? null : (double)LinesHit / LinesTotal;

    /// <summary>
    /// Per-line hit counts (line number → hits). When the same method entry appears in several
    /// <c>&lt;class&gt;</c> blocks or several merged report files, the parser reconciles per line
    /// with <c>Math.Max</c> — the same union-with-max semantics as the class-level parse.
    /// </summary>
    public IReadOnlyDictionary<int, int> LineHits { get => field ?? NoHits; init; } = NoHits;
}
