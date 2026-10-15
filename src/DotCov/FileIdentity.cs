using static System.FormattableString;

namespace DotCov;

/// <summary>How a relative class filename resolves in a document that declares several <c>&lt;source&gt;</c> roots.</summary>
public enum FileIdentityMode
{
    /// <summary>
    /// Lexical only, the default: the same XML yields the same keys and warnings on every
    /// machine. A relative filename under several roots keeps a deterministic key (the first
    /// root) but its identity is unresolved: the report lists it in
    /// <see cref="CoverageReport.AmbiguousFiles"/>, and every gate answers
    /// <see cref="GateOutcome.NoData"/> until a mode below resolves it.
    /// </summary>
    Lexical,

    /// <summary>
    /// Resolve against the root under which the file exists on the READING machine's disk. A
    /// unique hit wins; none or several fall back to the first root with one document-level
    /// <see cref="CoverageWarningKind.FileIdentityAmbiguous"/> warning. The result depends on
    /// where the report is read, so run it where the sources are checked out.
    /// </summary>
    ProbeSources,

    /// <summary>
    /// Resolve against one root chosen explicitly (<see cref="FileIdentity.SourceRoot"/>). Every
    /// document that declares several roots must declare it, and at least one document must;
    /// otherwise parsing throws <see cref="UndeclaredSourceRootException"/>.
    /// </summary>
    SourceRoot,
}

/// <summary>
/// The file-identity policy of one parse, passed to every <see cref="CoberturaParser"/> entry
/// point; null there means <see cref="Lexical"/>. A value, never process-global state, so two
/// parses with different policies cannot affect each other. Single-root and root-less documents
/// and absolute filenames resolve the same way under every mode.
/// </summary>
public sealed class FileIdentity
{
    private FileIdentity(FileIdentityMode mode, string? sourceRoot)
    {
        Mode = mode;
        SourceRoot = sourceRoot;
    }

    /// <summary>The deterministic default; see <see cref="FileIdentityMode.Lexical"/>.</summary>
    public static FileIdentity Lexical { get; } = new(FileIdentityMode.Lexical, null);

    /// <summary>Opt-in disk probing; see <see cref="FileIdentityMode.ProbeSources"/>.</summary>
    public static FileIdentity ProbeSources { get; } = new(FileIdentityMode.ProbeSources, null);

    /// <summary>
    /// Resolve relative filenames against <paramref name="root"/>, compared with the declared
    /// roots after the same normalization (<c>c:\repo\</c> matches <c>C:/repo</c>; <c>.</c> names
    /// the no-op root).
    /// </summary>
    public static FileIdentity FromSourceRoot(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        return new FileIdentity(FileIdentityMode.SourceRoot, PathIdentity.NormalizeRoot(root));
    }

    public FileIdentityMode Mode { get; }

    /// <summary>The normalized chosen root for <see cref="FileIdentityMode.SourceRoot"/>; null otherwise.</summary>
    public string? SourceRoot { get; }
}

/// <summary>
/// A file whose relative filename matched several declared roots under
/// <see cref="FileIdentityMode.Lexical"/>: it is keyed under the first, but nothing proves that
/// root is its own.
/// </summary>
/// <param name="Path">The deterministic key the file was recorded under (<see cref="FileCoverage.Path"/>).</param>
/// <param name="CandidateRoots">Every normalized root the filename could belong to, in document order.</param>
public readonly record struct AmbiguousFile(string Path, IReadOnlyList<string> CandidateRoots)
{
    /// <summary>
    /// The NoData reason every gate gives while files stay ambiguous: <c>check</c>, <c>test</c>,
    /// <c>crap</c> and the Fallout target say the same thing.
    /// </summary>
    internal static string GateReason(IReadOnlyList<AmbiguousFile> files)
    {
        var roots = files.SelectMany(static f => f.CandidateRoots).Distinct(StringComparer.Ordinal).Count();
        return Invariant($"{files.Count} {(files.Count is 1 ? "file has" : "files have")} ambiguous identity across {roots} source roots; ") +
               "re-run with --probe-sources on the machine that holds the sources or supply --source-root";
    }

    /// <summary>One entry per path, candidate roots unioned in first-seen order: the merge rule for every report type.</summary>
    internal static List<AmbiguousFile> Union(IEnumerable<AmbiguousFile> files) =>
        files.GroupBy(static f => f.Path, StringComparer.Ordinal)
            .Select(static g => new AmbiguousFile(g.Key,
                g.SelectMany(static f => f.CandidateRoots).Distinct(StringComparer.Ordinal).ToArray()))
            .ToList();

    /// <summary>A root as diagnostics print it: the no-op sentinel <c>""</c> reads as <c>.</c>.</summary>
    internal static string Display(string root) => root.Length is 0 ? "." : root;
}

/// <summary>
/// <see cref="FileIdentity.FromSourceRoot"/> named a root a report does not declare: a document
/// with several roots lacks it, or no document declares it at all. An invalid request rather
/// than an unreadable report, so the CLI exits with its usage code.
/// </summary>
public sealed class UndeclaredSourceRootException(string sourceRoot, IReadOnlyList<string> declaredRoots, string? sourceName)
    : Exception(BuildMessage(sourceRoot, declaredRoots, sourceName))
{
    /// <summary>The normalized root that was asked for.</summary>
    public string SourceRoot { get; } = sourceRoot;

    /// <summary>The normalized roots that were declared: by <see cref="SourceName"/>, or by every report read.</summary>
    public IReadOnlyList<string> DeclaredRoots { get; } = declaredRoots;

    /// <summary>The report that lacks the root; null when no report of the parse declares it.</summary>
    public string? SourceName { get; } = sourceName;

    private static string BuildMessage(string sourceRoot, IReadOnlyList<string> declaredRoots, string? sourceName)
    {
        var declared = declaredRoots.Count is 0
            ? "none"
            : string.Join(", ", declaredRoots.Select(static r => $"'{AmbiguousFile.Display(r)}'"));
        return sourceName is null
            ? $"source root '{AmbiguousFile.Display(sourceRoot)}' is not declared by any report (declared: {declared})."
            : $"source root '{AmbiguousFile.Display(sourceRoot)}' is not declared by {sourceName} (declared: {declared}).";
    }
}
