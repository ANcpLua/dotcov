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
/// <item><c>Type`2</c> → <c>Type</c> (generic arity suffix, display only: <c>Type</c> and <c>Type`2</c> stay distinct identities)</item>
/// <item><c>&lt;&lt;-ctor&gt;b__0_0&gt;d</c>, <c>&lt;MyApp-IJob-RunAsync&gt;d__1</c> → <c>.ctor</c>, <c>MyApp.IJob.RunAsync</c>
/// (Roslyn spells <c>.</c> as <c>-</c> inside synthesized TYPE names; method names keep the dot)</item>
/// </list>
/// Mangled names nest (an async lambda's state machine is <c>&lt;&lt;M&gt;b__0_0&gt;d</c>), so every
/// bracket parse here matches the OUTER close bracket and re-normalizes the inner name.
/// Folded methods lose their arity (a state machine's <c>MoveNext()</c> says nothing about the
/// origin's parameter list), which the matcher records as "unknown" rather than inventing one.
/// What they keep is the origin's METHOD ORDINAL — Roslyn numbers every source method of a type
/// and stamps that number into everything it synthesizes for it (<c>&lt;M&gt;d__3</c>,
/// <c>&lt;M&gt;b__3_0</c>, <c>&lt;M&gt;g__Local|3_0</c>, <c>&lt;&gt;c__DisplayClass3_0</c>), so two
/// overloads' synthetic material never shares an origin (<see cref="SyntheticOrigin"/>).
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
    /// <param name="typeKey">Display type identity, generic arity stripped: <c>Ns.Outer.Inner</c>.</param>
    /// <param name="typeIdentity">
    /// The same chain with generic arity kept (<c>Ns.Result`1</c>): <c>Result</c> and
    /// <c>Result&lt;T&gt;</c> share a display name but are different types whose methods must
    /// never merge.
    /// </param>
    /// <param name="folded">
    /// True when the identity came from a synthetic container, meaning the signature/arity of
    /// the raw entry does not describe the origin method.
    /// </param>
    internal static bool TryNormalize(
        string className, string methodName,
        out string typeKey, out string typeIdentity, out string methodKey, out SyntheticOrigin? origin)
    {
        typeKey = "";
        typeIdentity = "";
        methodKey = "";
        origin = null;

        var chain = ScanClassChain(className);

        if (chain.TypeSegments.Count is 0) return false;
        typeIdentity = string.Join(".", chain.TypeSegments);
        typeKey = string.Join(".", chain.TypeSegments.Select(StripGenericArity));

        if (chain.StateMachine is { } machine)
        {
            // Only MoveNext carries the method body; .ctor/SetStateMachine/Dispose/Reset/
            // get_Current are machinery.
            if (methodName is not "MoveNext") return false;
            methodKey = machine.Name;
            origin = new SyntheticOrigin(machine.Ordinal ?? chain.ContainerOrdinal ?? "", machine.OwnBody);
            return true;
        }

        if (chain.InSyntheticContainer)
        {
            // Only mangled members (<M>b__…, <M>g__…) name an origin; a display class's own
            // .ctor or cached-delegate field accessors do not.
            if (!TryMangledOrigin(methodName, out var name, out var ordinal)) return false;
            methodKey = name;
            origin = new SyntheticOrigin(ordinal ?? chain.ContainerOrdinal ?? "", OwnBody: false);
            return true;
        }

        // The top-level entry point is a source method, not a synthetic group with an
        // unknown ordinal. Its state machine can then attach to this direct entry.
        if (methodName is "<Main>$")
        {
            methodKey = "Main";
            return true;
        }

        // On the source type itself: this-capturing lambdas (<M>b__…) and local functions
        // (<M>g__Name|…) are emitted directly on the type with mangled names.
        if (TryMangledOrigin(methodName, out var mangled, out var mangledOrdinal))
        {
            methodKey = mangled;
            origin = new SyntheticOrigin(mangledOrdinal ?? "", OwnBody: false);
            return true;
        }

        methodKey = methodName;
        return true;
    }

    /// <summary>
    /// The nested-class chain of an IL class name: its real type segments (generic arity kept),
    /// the state machine that terminates it, if any, whether a synthetic container was crossed,
    /// and the method ordinal that container carries.
    /// </summary>
    private readonly record struct ClassChain(
        List<string> TypeSegments,
        (string Name, string? Ordinal, bool OwnBody)? StateMachine,
        bool InSyntheticContainer,
        string? ContainerOrdinal);

    /// <summary>
    /// Walk the nested-class chain of an IL class name, collecting real type segments
    /// (generic arity kept) until a state-machine segment terminates the chain — anything
    /// nested deeper (a lambda inside an async method compiles into the state machine) is still
    /// that method's machinery. Synthetic containers (<c>&lt;&gt;c</c>,
    /// <c>&lt;&gt;c__DisplayClass0_0</c>, <c>&lt;&gt;o__</c> dynamic call sites,
    /// <c>&lt;&gt;f__AnonymousType…</c>) contribute no type segment of their own but are NOT
    /// terminal: an async lambda's state machine nests INSIDE its display class
    /// (<c>Type/&lt;&gt;c/&lt;&lt;M&gt;b__0_0&gt;d</c>), so deeper segments must still be scanned.
    /// </summary>
    private static ClassChain ScanClassChain(string className)
    {
        var typeSegments = new List<string>();
        var inSyntheticContainer = false;
        string? containerOrdinal = null;

        foreach (var segment in className.Split('/'))
        {
            if (TryStateMachineOrigin(segment, out var name, out var ordinal, out var ownBody))
                return new ClassChain(typeSegments, (name, ordinal, ownBody), inSyntheticContainer, containerOrdinal);

            if (segment.StartsWith("<>", StringComparison.Ordinal))
            {
                inSyntheticContainer = true;
                // <>c__DisplayClass3_0: the closure of method #3.
                if (segment.StartsWith(DisplayClassPrefix, StringComparison.Ordinal))
                    containerOrdinal = MethodOrdinal(segment.AsSpan(DisplayClassPrefix.Length));
                continue;
            }

            // Anything else nested inside a synthetic container is machinery, not a type name.
            if (inSyntheticContainer) continue;

            typeSegments.Add(segment);
        }

        return new ClassChain(typeSegments, null, inSyntheticContainer, containerOrdinal);
    }

    private const string DisplayClassPrefix = "<>c__DisplayClass";

    /// <summary>
    /// The member name a Roslyn metrics display carries for a normalized method key.
    /// MinimallyQualifiedFormat drops an explicit implementation's interface (IL
    /// <c>System.IDisposable.Dispose</c> displays as <c>void Bag.Dispose()</c>) and shows a static
    /// constructor exactly like a parameterless instance one (<c>Calc.Calc()</c>, read as
    /// <c>.ctor</c>), so those are the names to look up.
    /// </summary>
    internal static string MetricsMemberName(string methodKey)
    {
        if (methodKey is ".cctor") return ".ctor";
        var dot = BalancedText.LastIndexOfTopLevel(methodKey, '.');
        return dot > 0 ? methodKey[(dot + 1)..] : methodKey;
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
    /// State-machine class segment → origin method name and ordinal. Shapes: <c>&lt;M&gt;d__3</c>
    /// (async/iterator method, ordinal 3 — the method's OWN body), <c>&lt;&lt;M&gt;b__0_0&gt;d</c>
    /// (async lambda — bare <c>d</c>, no counter; ordinal from the lambda name),
    /// <c>&lt;&lt;M&gt;g__Local|0_0&gt;d</c> (async/iterator local function),
    /// <c>&lt;&lt;Main&gt;$&gt;d__0</c> (top-level statements).
    /// </summary>
    private static bool TryStateMachineOrigin(string segment, out string name, out string? ordinal, out bool ownBody)
    {
        name = "";
        ordinal = null;
        ownBody = false;
        if (segment.Length < 4 || segment[0] is not '<') return false;
        var close = MatchingClose(segment);
        if (close <= 1) return false;                              // "<>…" has no origin name
        var rest = segment.AsSpan(close + 1);
        if (!rest.SequenceEqual("d") && !rest.StartsWith("d__", StringComparison.Ordinal)) return false;

        var inner = segment[1..close];
        // A plain inner name is the method itself, so the state machine IS that method's body;
        // a mangled one (a lambda, a local function, <Main>$) is machinery inside it.
        ownBody = !TryMangledOrigin(inner, out var innerName, out var innerOrdinal);
        // A type name spells the origin's dots as '-' (`<<-ctor>b__0_0>d`,
        // `<System-Collections-IEnumerable-GetEnumerator>d__3`); '-' is no identifier character.
        name = (ownBody ? inner : innerName).Replace('-', '.');
        ordinal = innerOrdinal ?? (rest.Length > 3 ? LeadingDigits(rest[3..]) : null);
        return true;
    }

    /// <summary>
    /// <c>&lt;M&gt;b__0_1</c> (lambda) / <c>&lt;M&gt;g__Name|0_0</c> (local function) /
    /// <c>&lt;Main&gt;$</c> (top-level statements) → origin <c>M</c> / <c>Main</c>, with the
    /// origin's method ordinal when the name carries one (<c>b__N_K</c>, <c>g__Name|N_K</c>; a
    /// display-class lambda's <c>b__K</c> numbers the lambda, not the method).
    /// </summary>
    private static bool TryMangledOrigin(string methodName, out string origin, out string? ordinal)
    {
        origin = "";
        ordinal = null;
        if (methodName.Length < 4 || methodName[0] is not '<') return false;
        var close = MatchingClose(methodName);
        if (close <= 1) return false;
        var rest = methodName.AsSpan(close + 1);
        if (rest.StartsWith("b__", StringComparison.Ordinal))
            ordinal = MethodOrdinal(rest[3..]);
        else if (rest.StartsWith("g__", StringComparison.Ordinal))
            ordinal = MethodOrdinal(rest[(rest.IndexOf('|') + 1)..]);
        else if (!rest.SequenceEqual("$"))
            return false;

        // A bracketed origin may itself be mangled (<RunAsync>b__0_0 inside an async-lambda
        // state machine, <Main>$ inside a top-level-statements local function): re-normalize
        // until a plain source name remains, keeping the first ordinal found.
        var inner = methodName[1..close];
        if (TryMangledOrigin(inner, out var innerOrigin, out var innerOrdinal))
        {
            origin = innerOrigin;
            ordinal ??= innerOrdinal;
        }
        else
        {
            origin = inner;
        }
        return true;
    }

    /// <summary>
    /// The method ordinal at the start of an <c>N_K</c> suffix (<c>0_1</c> → <c>0</c>); null for
    /// any other shape, including a bare lambda counter.
    /// </summary>
    private static string? MethodOrdinal(ReadOnlySpan<char> suffix) =>
        LeadingDigits(suffix) is { } digits && suffix.Length > digits.Length && suffix[digits.Length] is '_'
            ? digits
            : null;

    /// <summary>The leading ASCII digits of <paramref name="text"/>; null when it starts with none.</summary>
    private static string? LeadingDigits(ReadOnlySpan<char> text)
    {
        var length = 0;
        while (length < text.Length && char.IsAsciiDigit(text[length])) length++;
        return length is 0 ? null : text[..length].ToString();
    }

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

/// <summary>
/// Where a folded (compiler-synthesized) method came from. <see cref="Ordinal"/> is the origin's
/// method ordinal ("" when the names carry none), so the synthetic material of
/// <c>M(int)</c> and <c>M(string)</c> stays apart; <see cref="OwnBody"/> marks an async/iterator
/// state machine that IS its origin's body — such a method's own (kickoff) entry carries no
/// lines, so a same-named entry WITH lines is a different overload.
/// </summary>
internal readonly record struct SyntheticOrigin(string Ordinal, bool OwnBody);
