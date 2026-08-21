namespace DotCov;

/// <summary>
/// Depth-aware scanning over type/member display text where <c>&lt;&gt;</c>, <c>[]</c>, and
/// <c>()</c> nest — the one home for "at top level" so <c>Dictionary&lt;string, int&gt;</c>
/// can never inflate an arity and a dot inside generic arguments can never split a name.
/// Shared by <see cref="MethodIdentity"/> (IL signatures) and <see cref="CodeMetricsReader"/>
/// (Roslyn display strings), which count parameters over the same nesting rules.
/// </summary>
internal static class BalancedText
{
    /// <summary>The one definition of what nests: +1 for an opener, −1 for a closer, else 0.</summary>
    private static int DepthDelta(char c)
    {
        if (c is '<' or '[' or '(') return 1;
        if (c is '>' or ']' or ')') return -1;
        return 0;
    }

    /// <summary>
    /// First index of <paramref name="target"/> at bracket depth 0, or −1. The target match is
    /// tested before the character adjusts depth, so a target of <c>'('</c> finds the opening
    /// parenthesis itself rather than skipping past it.
    /// </summary>
    internal static int IndexOfTopLevel(ReadOnlySpan<char> text, char target)
    {
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (depth is 0 && text[i] == target) return i;
            depth += DepthDelta(text[i]);
        }
        return -1;
    }

    /// <summary>Last index of <paramref name="target"/> at bracket depth 0, or −1.</summary>
    internal static int LastIndexOfTopLevel(ReadOnlySpan<char> text, char target)
    {
        var depth = 0;
        var last = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (depth is 0 && text[i] == target) last = i;
            depth += DepthDelta(text[i]);
        }
        return last;
    }

    /// <summary>
    /// Number of top-level comma-separated items in <paramref name="list"/> (a parameter list
    /// with the surrounding parentheses already removed); 0 for blank.
    /// </summary>
    internal static int CountTopLevelItems(ReadOnlySpan<char> list)
    {
        if (list.Trim().Length is 0) return 0;

        var depth = 0;
        var count = 1;
        foreach (var c in list)
        {
            if (depth is 0 && c is ',') count++;
            depth += DepthDelta(c);
        }
        return count;
    }
}
