namespace DotCov;

/// <summary>
/// Depth-aware scanning over type/member display text where <c>&lt;&gt;</c>, <c>[]</c>, and
/// <c>()</c> nest — the one home for "at top level" so <c>Dictionary&lt;string, int&gt;</c>
/// can never inflate an arity and a dot inside generic arguments can never split a name.
/// Shared by <see cref="MethodIdentity"/> (IL signatures) and <see cref="CodeMetricsReader"/>
/// (Roslyn display strings), which count parameters over the same nesting rules.
/// <para>
/// String and char literals are opaque: Roslyn displays parameter default values
/// (<c>string sep = ", "</c>, <c>char open = '&lt;'</c>), and the commas and brackets inside a
/// literal are text, not structure. IL signatures never contain quotes.
/// </para>
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
    /// Index of the quote closing the string or char literal opened at <paramref name="open"/>,
    /// honoring backslash escapes (<c>"a\"b"</c>, <c>'\''</c>); the last index when the literal
    /// never closes, so an unterminated literal swallows the rest of the text.
    /// </summary>
    private static int LiteralEnd(ReadOnlySpan<char> text, int open)
    {
        for (var i = open + 1; i < text.Length; i++)
        {
            if (text[i] is '\\') i++;
            else if (text[i] == text[open]) return i;
        }
        return text.Length - 1;
    }

    /// <summary>Last index of <paramref name="target"/> at bracket depth 0, outside literals, or −1.</summary>
    internal static int LastIndexOfTopLevel(ReadOnlySpan<char> text, char target)
    {
        var depth = 0;
        var last = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (depth is 0 && text[i] == target) last = i;
            if (text[i] is '"' or '\'') i = LiteralEnd(text, i);
            else depth += DepthDelta(text[i]);
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
        for (var i = 0; i < list.Length; i++)
        {
            if (depth is 0 && list[i] is ',') count++;
            if (list[i] is '"' or '\'') i = LiteralEnd(list, i);
            else depth += DepthDelta(list[i]);
        }
        return count;
    }

    internal static IEnumerable<string> SplitTopLevel(string list)
    {
        if (list.Trim().Length is 0) yield break;
        var depth = 0;
        var start = 0;
        for (var i = 0; i < list.Length; i++)
        {
            if (depth is 0 && list[i] is ',')
            {
                yield return list[start..i].Trim();
                start = i + 1;
            }
            if (list[i] is '"' or '\'') i = LiteralEnd(list, i);
            else depth += DepthDelta(list[i]);
        }
        yield return list[start..].Trim();
    }
}
