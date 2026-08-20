namespace DotCov;

/// <summary>
/// The shared file/root identity helpers behind the parser's key resolution
/// (<c>CoberturaParser.ConsumeSource</c>), the merge's cross-convention ambiguity scan
/// (<c>CoverageReport.WarnOnAmbiguousFileIdentity</c>), and the diff's unique-file-name
/// pairing fallback (<c>CoverageDiff</c>). One home so the three semantics can never
/// silently desynchronize — <see cref="FileNameOf"/> previously existed as two
/// byte-identical private copies, both load-bearing for the same identity contract.
/// </summary>
internal static class PathIdentity
{
    /// <summary>Final path segment of a separator-normalized path (<c>src/App.cs</c> → <c>App.cs</c>).</summary>
    internal static string FileNameOf(string path) => path[(path.LastIndexOf('/') + 1)..];

    /// <summary>
    /// Canonical form of a declared <c>&lt;source&gt;</c> root: trimmed, separators flipped
    /// to <c>/</c>, a leading <c>./</c> stripped, the no-op spellings (<c>.</c>, <c>./</c>,
    /// empty) collapsed to <c>""</c> (the no-op sentinel), a lowercase drive letter
    /// uppercased (the same rule the parser applies to file KEYS, so root identity and key
    /// identity agree), and trailing <c>/</c> trimmed — but never below one character, so
    /// Coverlet's bare <c>&lt;source&gt;/&lt;/source&gt;</c> root survives intact and the
    /// documented <c>/</c>-vs-<c>/_/</c> cross-convention detection keeps its operands.
    /// Applied at parse time in <c>ConsumeSource</c> AND at comparison time in
    /// <see cref="RootsDiffer"/>, so hand-built reports whose roots were never
    /// parser-normalized still compare by identity rather than spelling.
    /// </summary>
    internal static string NormalizeRoot(string root)
    {
        root = root.Trim().Replace('\\', '/');
        if (root.StartsWith("./", StringComparison.Ordinal)) root = root[2..];
        if (root is ".") return "";
        if (root.Length >= 3 && char.IsAsciiLetterLower(root[0]) && root[1] == ':' && root[2] == '/')
            root = char.ToUpperInvariant(root[0]) + root[1..];
        while (root.Length > 1 && root.EndsWith('/'))
            root = root[..^1];
        return root;
    }

    /// <summary>
    /// Whether two reports' declared roots genuinely differ — the single predicate behind
    /// both the merge's ambiguity scan and the diff's unconditional name-pairing arm, so
    /// the two can never disagree about what counts as a convention change. Compares
    /// normalized SEQUENCES, not sets: root order is semantic (relative filenames resolve
    /// against the FIRST root), so the same roots reordered can key a relative file
    /// differently and must count as differing. Root-less on both sides is "same" —
    /// hand-built reports never trip the cross-convention machinery.
    /// </summary>
    internal static bool RootsDiffer(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (a.Count is 0 && b.Count is 0) return false;
        return !a.Select(NormalizeRoot).SequenceEqual(b.Select(NormalizeRoot), StringComparer.Ordinal);
    }
}
