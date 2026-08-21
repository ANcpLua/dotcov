namespace DotCov;

/// <summary>
/// The compiler-name-mangling normalization table behind <see cref="CrapAnalysis.Analyze"/>:
/// maps coverlet's IL-level (class, method) pairs back to the source method a human (or an
/// agent refactoring toward a green gate) would name. One home for every mangling rule so the
/// matcher and any future consumer can never disagree about what a synthetic name means.
/// <para>
/// The table (Roslyn's documented private naming scheme, stable across compiler versions):
/// <list type="bullet">
/// <item><c>Ns.Type/&lt;M&gt;d__3</c> + <c>MoveNext</c> → <c>Ns.Type.M</c> (async/iterator state machine)</item>
/// <item><c>Ns.Type/&lt;&gt;c</c> or <c>&lt;&gt;c__DisplayClass0_0</c> + <c>&lt;M&gt;b__0_1</c> → <c>Ns.Type.M</c> (lambda)</item>
/// <item><c>&lt;M&gt;g__Local|0_0</c> → <c>M</c> (local function, on the type or a display class)</item>
/// <item><c>Type`2</c> → <c>Type</c> (generic arity suffix)</item>
/// </list>
/// Folded methods lose their arity (a state machine's <c>MoveNext()</c> says nothing about the
/// origin's parameter list), which the matcher records as "unknown" rather than inventing one.
/// </para>
/// </summary>
internal static class MethodIdentity
{
    /// <summary>
    /// Normalize an IL-level identity to its source-method identity. Returns <c>false</c> for
    /// pure compiler infrastructure with no source method to fold into — display-class
    /// constructors, <c>SetStateMachine</c>, the non-<c>MoveNext</c> surface of iterator state
    /// machines, anonymous-type members — whose lines belong to no method a source file names.
    /// </summary>
    /// <param name="folded">
    /// True when the identity came from a synthetic container, meaning the signature/arity of
    /// the raw entry does not describe the origin method.
    /// </param>
    internal static bool TryNormalize(
        string className, string methodName, out string typeKey, out string methodKey, out bool folded)
    {
        typeKey = "";
        methodKey = "";
        folded = false;

        var typeSegments = new List<string>();
        string? stateMachineOrigin = null;
        var inSyntheticContainer = false;

        foreach (var segment in className.Split('/'))
        {
            if (TryStateMachineOrigin(segment, out var origin))
            {
                // The state-machine segment terminates the chain: anything nested deeper
                // (a lambda inside an async method compiles into the state machine) is still
                // that method's machinery.
                stateMachineOrigin = origin;
                break;
            }

            if (segment.StartsWith("<>", StringComparison.Ordinal))
            {
                // <>c, <>c__DisplayClass0_0, <>o__ (dynamic call sites), <>f__AnonymousType…:
                // synthetic containers that contribute no type segment of their own.
                inSyntheticContainer = true;
                break;
            }

            typeSegments.Add(StripGenericArity(segment));
        }

        if (typeSegments.Count is 0) return false;
        typeKey = string.Join(".", typeSegments);

        if (stateMachineOrigin is not null)
        {
            // Only MoveNext carries the method body; .ctor/SetStateMachine/Dispose/Reset/
            // get_Current are machinery.
            if (methodName is not "MoveNext") return false;
            methodKey = stateMachineOrigin;
            folded = true;
            return true;
        }

        if (inSyntheticContainer)
        {
            // Only mangled members (<M>b__…, <M>g__…) name an origin; a display class's own
            // .ctor or cached-delegate field accessors do not.
            if (!TryMangledOrigin(methodName, out var origin)) return false;
            methodKey = origin;
            folded = true;
            return true;
        }

        // On the source type itself: this-capturing lambdas (<M>b__…) and local functions
        // (<M>g__Name|…) are emitted directly on the type with mangled names.
        if (TryMangledOrigin(methodName, out var mangledOrigin))
        {
            methodKey = mangledOrigin;
            folded = true;
            return true;
        }

        methodKey = methodName;
        return true;
    }

    /// <summary>
    /// Parameter count from a coverlet IL signature like <c>(System.Int32,System.String)</c>,
    /// counting commas outside <c>&lt;&gt;</c>/<c>[]</c> nesting. Null when the signature is
    /// absent or not parenthesized — "unknown", never zero.
    /// </summary>
    internal static int? SignatureArity(string signature)
    {
        if (signature.Length < 2 || signature[0] is not '(' || signature[^1] is not ')')
            return null;

        var inner = signature.AsSpan(1, signature.Length - 2).Trim();
        if (inner.Length is 0) return 0;

        var depth = 0;
        var count = 1;
        foreach (var c in inner)
        {
            if (c is '<' or '[' or '(') depth++;
            else if (c is '>' or ']' or ')') depth--;
            else if (c is ',' && depth is 0) count++;
        }
        return count;
    }

    /// <summary><c>&lt;M&gt;d__3</c>-shaped state-machine class segment → origin method name <c>M</c>.</summary>
    private static bool TryStateMachineOrigin(string segment, out string origin)
    {
        origin = "";
        if (segment.Length < 4 || segment[0] is not '<') return false;
        var close = segment.IndexOf('>');
        if (close <= 1) return false;                              // "<>…" has no origin name
        if (!segment.AsSpan(close + 1).StartsWith("d__", StringComparison.Ordinal)) return false;
        origin = segment[1..close];
        return true;
    }

    /// <summary><c>&lt;M&gt;b__0_1</c> (lambda) / <c>&lt;M&gt;g__Name|0_0</c> (local function) → origin <c>M</c>.</summary>
    private static bool TryMangledOrigin(string methodName, out string origin)
    {
        origin = "";
        if (methodName.Length < 4 || methodName[0] is not '<') return false;
        var close = methodName.IndexOf('>');
        if (close <= 1) return false;
        var rest = methodName.AsSpan(close + 1);
        if (!rest.StartsWith("b__", StringComparison.Ordinal) &&
            !rest.StartsWith("g__", StringComparison.Ordinal)) return false;
        origin = methodName[1..close];
        return true;
    }

    private static string StripGenericArity(string segment)
    {
        var tick = segment.IndexOf('`');
        return tick < 0 ? segment : segment[..tick];
    }
}
