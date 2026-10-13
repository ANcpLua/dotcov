namespace DotCov;

/// <summary>
/// The shared file/root identity helpers behind the parser's key resolution
/// (<c>CoberturaParser.ConsumeSource</c>), the merge's cross-convention ambiguity scan
/// (<c>CoverageReport.WarnOnAmbiguousFileIdentity</c>), and the diff's unique-file-name
/// pairing fallback (<c>CoverageDiff</c>). One home so the three semantics can never
/// silently desynchronize.
/// </summary>
internal static class PathIdentity
{
    /// <summary>Final path segment of a separator-normalized path (<c>src/App.cs</c> → <c>App.cs</c>).</summary>
    internal static string FileNameOf(string path) => path[(path.LastIndexOf('/') + 1)..];

    /// <summary>
    /// Canonical form of a declared <c>&lt;source&gt;</c> root: trimmed, separators flipped
    /// to <c>/</c>, redundant <c>.</c> segments removed by <see cref="RemoveDotSegments"/>
    /// (the rule file keys follow too), the no-op spellings (<c>.</c>, <c>./</c>, empty)
    /// collapsed to <c>""</c> (the no-op sentinel), a lowercase drive letter uppercased (the
    /// same rule the parser applies to file KEYS, so root identity and key identity agree),
    /// and trailing <c>/</c> trimmed — but never below one character, so Coverlet's bare
    /// <c>&lt;source&gt;/&lt;/source&gt;</c> root survives intact and the documented
    /// <c>/</c>-vs-<c>/_/</c> cross-convention detection keeps its operands.
    /// Applied at parse time in <c>ConsumeSource</c> AND at comparison time in
    /// <see cref="RootsDiffer"/>, so hand-built reports whose roots were never
    /// parser-normalized still compare by identity rather than spelling.
    /// </summary>
    internal static string NormalizeRoot(string root)
    {
        root = UppercaseDriveLetter(RemoveDotSegments(root.Trim().Replace('\\', '/')));
        while (root.Length > 1 && root.EndsWith('/'))
            root = root[..^1];
        return root;
    }

    /// <summary>
    /// Drop every redundant <c>.</c> segment of a <c>/</c>-separated path — a leading
    /// <c>./</c>, an inner <c>/./</c>, a trailing <c>/.</c> — so <c>./src/C.cs</c>,
    /// <c>src/./C.cs</c> and <c>src/C.cs</c> name one file. Lexical only: <c>..</c> is kept
    /// (resolving it needs the file system's view of links) and the disk is never read. A
    /// path of only dot segments (<c>.</c>, <c>./</c>) becomes <c>""</c>, the no-op root.
    /// </summary>
    internal static string RemoveDotSegments(string path)
    {
        // A separator doubled after a leading "./" (".//src") is part of that no-op prefix; left
        // in place it would turn the relative name into an absolute one.
        while (path.StartsWith("./", StringComparison.Ordinal))
            path = path[2..].TrimStart('/');
        while (path.Contains("/./", StringComparison.Ordinal))
            path = path.Replace("/./", "/", StringComparison.Ordinal);
        if (path.EndsWith("/.", StringComparison.Ordinal))
            path = path[..^1];
        return path is "." ? "" : path;
    }

    /// <summary>
    /// Uppercase a leading drive letter, so <c>c:/x/A.cs</c> and <c>C:/x/A.cs</c> — the same
    /// file emitted by different Windows toolchains — are one Ordinal key. Key normalization,
    /// not a case-insensitive comparer: a Dictionary has a single comparer for every key.
    /// </summary>
    internal static string UppercaseDriveLetter(string path) =>
        path.Length >= 3 && char.IsAsciiLetterLower(path[0]) && path[1] == ':' && path[2] == '/'
            ? char.ToUpperInvariant(path[0]) + path[1..]
            : path;

    /// <summary>
    /// Whether two reports' declared roots genuinely differ — the single predicate behind
    /// both the merge's ambiguity scan and the diff's unconditional name-pairing arm, so
    /// the two can never disagree about what counts as a convention change. Compares
    /// normalized SEQUENCES, not sets: root order is semantic (a relative filename falls back to
    /// the FIRST root when the disk cannot decide), so the same roots reordered can key a
    /// relative file differently and must count as differing. Root-less on both sides is "same" —
    /// hand-built reports never trip the cross-convention machinery.
    /// </summary>
    internal static bool RootsDiffer(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count is 0 && b.Count is 0) return false;
        return !a.Select(NormalizeRoot).SequenceEqual(b.Select(NormalizeRoot), StringComparer.Ordinal);
    }
}
