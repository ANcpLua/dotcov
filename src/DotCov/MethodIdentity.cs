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
/// <item><c>Ns.Type/&lt;&gt;c/&lt;&lt;M&gt;b__0_0&gt;d</c> + <c>MoveNext</c> → <c>Ns.Type.M</c> (async-lambda state machine, nested inside its display class)</item>
/// <item><c>&lt;M&gt;g__Local|0_0</c> → <c>M</c> (local function, on the type or a display class)</item>
/// <item><c>&lt;&lt;M&gt;g__Local|0_0&gt;d</c> + <c>MoveNext</c> → <c>M</c> (async/iterator local-function state machine)</item>
/// <item><c>Program/&lt;Main&gt;$</c> and <c>Program/&lt;&lt;Main&gt;$&gt;d__0</c> + <c>MoveNext</c> → <c>Program.Main</c> (top-level statements)</item>
/// <item><c>Type`2</c> → <c>Type</c> (generic arity suffix)</item>
/// </list>
/// Mangled names nest (an async lambda's state machine is <c>&lt;&lt;M&gt;b__0_0&gt;d</c>), so every
/// bracket parse here matches the OUTER close bracket and re-normalizes the inner name.
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

        var (typeSegments, stateMachineOrigin, inSyntheticContainer) = ScanClassChain(className);

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
    /// Walk the nested-class chain of an IL class name, collecting real type segments
    /// (generic arity stripped) until a state-machine segment terminates the chain — anything
    /// nested deeper (a lambda inside an async method compiles into the state machine) is still
    /// that method's machinery. Synthetic containers (<c>&lt;&gt;c</c>,
    /// <c>&lt;&gt;c__DisplayClass0_0</c>, <c>&lt;&gt;o__</c> dynamic call sites,
    /// <c>&lt;&gt;f__AnonymousType…</c>) contribute no type segment of their own but are NOT
    /// terminal: an async lambda's state machine nests INSIDE its display class
    /// (<c>Type/&lt;&gt;c/&lt;&lt;M&gt;b__0_0&gt;d</c>), so deeper segments must still be scanned.
    /// </summary>
    private static (List<string> TypeSegments, string? StateMachineOrigin, bool InSyntheticContainer)
        ScanClassChain(string className)
    {
        var typeSegments = new List<string>();
        var inSyntheticContainer = false;

        foreach (var segment in className.Split('/'))
        {
            if (TryStateMachineOrigin(segment, out var origin))
                return (typeSegments, origin, inSyntheticContainer);

            if (segment.StartsWith("<>", StringComparison.Ordinal))
            {
                inSyntheticContainer = true;
                continue;
            }

            // Anything else nested inside a synthetic container is machinery, not a type name.
            if (inSyntheticContainer) continue;

            typeSegments.Add(StripGenericArity(segment));
        }

        return (typeSegments, null, inSyntheticContainer);
    }

    /// <summary>
    /// Parameter count from a coverlet IL signature like <c>(System.Int32,System.String)</c>,
    /// counting commas outside <c>&lt;&gt;</c>/<c>[]</c>/<c>()</c> nesting (see
    /// <see cref="BalancedText.CountTopLevelItems"/>). Null when the signature is absent or not
    /// parenthesized — "unknown", never zero.
    /// </summary>
    internal static int? SignatureArity(string signature)
    {
        if (signature.Length < 2 || signature[0] is not '(' || signature[^1] is not ')')
            return null;

        return BalancedText.CountTopLevelItems(signature.AsSpan(1, signature.Length - 2));
    }

    /// <summary>
    /// State-machine class segment → origin method name. Shapes: <c>&lt;M&gt;d__3</c>
    /// (async/iterator method), <c>&lt;&lt;M&gt;b__0_0&gt;d</c> (async lambda — bare <c>d</c>,
    /// no counter), <c>&lt;&lt;M&gt;g__Local|0_0&gt;d</c> (async/iterator local function),
    /// <c>&lt;&lt;Main&gt;$&gt;d__0</c> (top-level statements).
    /// </summary>
    private static bool TryStateMachineOrigin(string segment, out string origin)
    {
        origin = "";
        if (segment.Length < 4 || segment[0] is not '<') return false;
        var close = MatchingClose(segment);
        if (close <= 1) return false;                              // "<>…" has no origin name
        var rest = segment.AsSpan(close + 1);
        if (!rest.SequenceEqual("d") && !rest.StartsWith("d__", StringComparison.Ordinal)) return false;
        origin = NormalizeOrigin(segment[1..close]);
        return true;
    }

    /// <summary>
    /// <c>&lt;M&gt;b__0_1</c> (lambda) / <c>&lt;M&gt;g__Name|0_0</c> (local function) /
    /// <c>&lt;Main&gt;$</c> (top-level statements) → origin <c>M</c> / <c>Main</c>.
    /// </summary>
    private static bool TryMangledOrigin(string methodName, out string origin)
    {
        origin = "";
        if (methodName.Length < 4 || methodName[0] is not '<') return false;
        var close = MatchingClose(methodName);
        if (close <= 1) return false;
        var rest = methodName.AsSpan(close + 1);
        if (!rest.StartsWith("b__", StringComparison.Ordinal) &&
            !rest.StartsWith("g__", StringComparison.Ordinal) &&
            !rest.SequenceEqual("$")) return false;
        origin = NormalizeOrigin(methodName[1..close]);
        return true;
    }

    /// <summary>
    /// A bracketed origin may itself be mangled (<c>&lt;RunAsync&gt;b__0_0</c> inside an
    /// async-lambda state machine, <c>&lt;Main&gt;$</c> inside a top-level-statements local
    /// function): re-normalize until a plain source name remains.
    /// </summary>
    private static string NormalizeOrigin(string origin) =>
        TryMangledOrigin(origin, out var inner) ? inner : origin;

    /// <summary>
    /// Index of the <c>&gt;</c> matching the leading <c>&lt;</c>, depth-tracked because Roslyn
    /// nests mangled names — <c>IndexOf('&gt;')</c> on <c>&lt;&lt;RunAsync&gt;b__0_0&gt;d</c>
    /// finds the INNER close and misparses. −1 when unbalanced.
    /// </summary>
    private static int MatchingClose(string name)
    {
        var depth = 0;
        for (var i = 0; i < name.Length; i++)
        {
            if (name[i] is '<') depth++;
            else if (name[i] is '>' && --depth is 0) return i;
        }
        return -1;
    }

    private static string StripGenericArity(string segment)
    {
        var tick = segment.IndexOf('`');
        return tick < 0 ? segment : segment[..tick];
    }
}
