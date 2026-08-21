using System.Collections.Frozen;
using System.Globalization;
using System.Xml;

namespace DotCov;

/// <summary>What kind of member a <see cref="CodeMetricsMember"/> was read from.</summary>
public enum CodeMetricsMemberKind
{
    /// <summary>An ordinary method, constructor, or operator.</summary>
    Method,

    /// <summary>A property/event accessor (<c>get</c>/<c>set</c>/<c>init</c>/<c>add</c>/<c>remove</c>).</summary>
    Accessor,

    /// <summary>A property aggregate (its complexity is the sum over its accessors).</summary>
    Property,

    /// <summary>A field (initializer complexity; no coverage counterpart of its own).</summary>
    Field,

    /// <summary>An event aggregate.</summary>
    Event,
}

/// <summary>
/// One member's cyclomatic complexity from a Microsoft.CodeAnalysis.Metrics report
/// (<c>dotnet msbuild /t:Metrics</c> with the <c>Microsoft.CodeAnalysis.Metrics</c> package).
/// </summary>
/// <param name="TypeName">
/// Full type identity: the enclosing <c>&lt;Namespace Name&gt;</c> plus the <c>&lt;NamedType Name&gt;</c>
/// chain (nested types are dotted), with generic parameter lists stripped —
/// e.g. <c>MyApp.Stack</c> for <c>&lt;NamedType Name="Stack&lt;T&gt;"&gt;</c> under <c>MyApp</c>.
/// </param>
/// <param name="MemberName">
/// Normalized member name in coverage spelling: <c>Add</c>, <c>.ctor</c>, <c>get_Value</c>,
/// <c>op_Equality</c>, <c>Item</c> for indexers — derived from the display-string
/// <c>Name</c> attribute so it can match Cobertura method names directly.
/// </param>
/// <param name="Kind">Member kind — accessors and methods are gate-matchable; aggregates are fallbacks.</param>
/// <param name="Arity">Parameter count when the display string carries a parameter list, else <c>null</c>.</param>
/// <param name="CyclomaticComplexity">Roslyn's cyclomatic complexity for the member.</param>
/// <param name="DisplayName">The raw <c>Name</c> attribute, kept verbatim for unmatched-member listings.</param>
public readonly record struct CodeMetricsMember(
    string TypeName,
    string MemberName,
    CodeMetricsMemberKind Kind,
    int? Arity,
    int CyclomaticComplexity,
    string DisplayName);

/// <summary>
/// Streaming reader for the Microsoft.CodeAnalysis.Metrics XML shape
/// (<c>CodeMetricsReport/Targets/Target/Assembly/Namespaces/Namespace/Types/NamedType/Members</c>,
/// with <c>Method</c>/<c>Field</c>/<c>Property</c>/<c>Event</c> members, properties carrying an
/// <c>Accessors</c> element of accessor <c>Method</c>s, and per-node
/// <c>&lt;Metric Name="CyclomaticComplexity" Value="N"/&gt;</c>). Shape verified against the
/// writer in dotnet/roslyn <c>src/RoslynAnalyzers/Tools/Metrics/MetricsOutputWriter.cs</c> and
/// real generated reports: member <c>Name</c> attributes are Roslyn
/// <c>SymbolDisplayFormat.MinimallyQualifiedFormat</c> strings such as
/// <c>Task&lt;string&gt; Type.LoadAsync(string fileName, int lineCount)</c>,
/// <c>Type.Type(int seed)</c> for constructors, and <c>string Type.Prop.get</c> for accessors.
/// Same XmlReader streaming + hardening ethos as <see cref="CoberturaParser"/>.
/// </summary>
public static class CodeMetricsReader
{
    private const long DefaultMaxChars = 50_000_000;

    public static IReadOnlyList<CodeMetricsMember> Parse(Stream stream, long maxChars = DefaultMaxChars)
    {
        // Same hardening rationale as CoberturaParser.CreateSecureSettings: DTD skipped without
        // processing, entities can never resolve, bounded document size.
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            MaxCharactersFromEntities = 1024,
            XmlResolver = null,
            IgnoreWhitespace = true,
            MaxCharactersInDocument = maxChars
        });
        return ParseCore(reader);
    }

    /// <summary>
    /// <see cref="Parse"/> from a file path, with the same path-prefixing
    /// <see cref="XmlException"/> rethrow contract as <see cref="CoberturaParser.ParseFile"/>.
    /// </summary>
    public static IReadOnlyList<CodeMetricsMember> ParseFile(string path, long maxChars = DefaultMaxChars)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Parse(stream, maxChars);
        }
        catch (XmlException ex)
        {
            throw new XmlException($"{path}: {ex.Message}", ex, ex.LineNumber, ex.LinePosition);
        }
    }

    private sealed class PendingMember(string display, string element)
    {
        public readonly string Display = display;
        public readonly string Element = element;
        public int? Complexity;
    }

    /// <summary>The document position ParseCore threads through its element handlers.</summary>
    private sealed class ParserState
    {
        public readonly List<CodeMetricsMember> Members = [];
        public string Namespace = "";
        public readonly List<string> TypeChain = [];        // generic-stripped dotted segments, e.g. "Outer.Inner"
        public readonly Stack<PendingMember> MemberStack = new();   // Property → its Accessor methods nest
    }

    private static IReadOnlyList<CodeMetricsMember> ParseCore(XmlReader reader)
    {
        var state = new ParserState();

        while (reader.Read())
        {
            if (reader.NodeType is XmlNodeType.Element) OnElementStart(reader, state);
            else if (reader.NodeType is XmlNodeType.EndElement) OnElementEnd(reader, state);
        }

        return state.Members;
    }

    private static void OnElementStart(XmlReader reader, ParserState state)
    {
        switch (reader.LocalName)
        {
            case "Namespace":
                state.Namespace = reader.GetAttribute("Name") ?? "";
                break;

            case "NamedType":
                // Empty elements raise no EndElement, so only a real subtree pushes.
                if (!reader.IsEmptyElement)
                    state.TypeChain.Add(StripGenericsPerSegment(reader.GetAttribute("Name") ?? ""));
                break;

            case "Metric":
                RecordComplexity(reader, state);
                break;

            default:
                if (IsMemberElement(reader.LocalName)) OpenMember(reader, state);
                break;
        }
    }

    private static void OnElementEnd(XmlReader reader, ParserState state)
    {
        switch (reader.LocalName)
        {
            case "Namespace":
                state.Namespace = "";
                break;

            case "NamedType":
                if (state.TypeChain.Count > 0) state.TypeChain.RemoveAt(state.TypeChain.Count - 1);
                break;

            default:
                if (IsMemberElement(reader.LocalName)) CloseMember(state);
                break;
        }
    }

    private static bool IsMemberElement(string localName) =>
        localName is "Method" or "Field" or "Property" or "Event";

    private static void OpenMember(XmlReader reader, ParserState state)
    {
        // A member outside a NamedType is not ours; an empty member element carries no
        // Metrics — nothing to record.
        if (state.TypeChain.Count > 0 && !reader.IsEmptyElement)
            state.MemberStack.Push(new PendingMember(reader.GetAttribute("Name") ?? "", reader.LocalName));
    }

    private static void CloseMember(ParserState state)
    {
        if (state.MemberStack.Count is 0) return;
        var pending = state.MemberStack.Pop();
        // A member without a CyclomaticComplexity metric has nothing to contribute to a
        // complexity table — skipped, not zeroed.
        if (pending.Complexity is { } complexity)
            state.Members.Add(Materialize(pending, state.Namespace, state.TypeChain, complexity));
    }

    private static void RecordComplexity(XmlReader reader, ParserState state)
    {
        // Assign to the innermost OPEN member: a Property's own Metrics arrive before its
        // <Accessors>, so accessor metrics can never leak upward.
        if (state.MemberStack.Count > 0 &&
            reader.GetAttribute("Name") is "CyclomaticComplexity" &&
            int.TryParse(reader.GetAttribute("Value"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            state.MemberStack.Peek().Complexity = value;
    }

    private static CodeMetricsMember Materialize(
        PendingMember pending, string ns, List<string> typeChain, int complexity)
    {
        var typeName = string.Join(".", typeChain);
        if (ns.Length > 0) typeName = $"{ns}.{typeName}";

        var enclosingSimpleName = SimpleName(typeChain[^1]);
        var (memberName, arity, kind) = ParseDisplay(pending.Display, pending.Element, enclosingSimpleName);

        return new CodeMetricsMember(typeName, memberName, kind, arity, complexity, pending.Display);
    }

    private static string SimpleName(string dottedType)
    {
        var idx = dottedType.LastIndexOf('.');
        return idx < 0 ? dottedType : dottedType[(idx + 1)..];
    }

    /// <summary>
    /// Strip generic parameter lists per dot-segment: <c>Outer&lt;T&gt;.Inner&lt;U&gt;</c> →
    /// <c>Outer.Inner</c>. Depth-tracked so dots inside angle brackets never split segments.
    /// </summary>
    private static string StripGenericsPerSegment(string name)
    {
        Span<char> buffer = name.Length <= 256 ? stackalloc char[name.Length] : new char[name.Length];
        var length = 0;
        var depth = 0;
        foreach (var c in name)
        {
            if (c is '<') { depth++; continue; }
            if (c is '>') { depth--; continue; }
            if (depth is 0) buffer[length++] = c;
        }
        return new string(buffer[..length]);
    }

    // ── Display-string identity ──────────────────────────────────────────────
    //
    // The Name attribute is a Roslyn MinimallyQualifiedFormat display string. The TYPE identity
    // deliberately comes from the enclosing Namespace/NamedType elements, never from the display
    // string — parsing a return type that may itself contain dots, spaces, and generics out of
    // the display is exactly the fragile arm this sidesteps. Only the MEMBER name is extracted.

    private static (string MemberName, int? Arity, CodeMetricsMemberKind Kind) ParseDisplay(
        string display, string element, string enclosingSimpleName)
    {
        var (head, arity) = SplitParameterList(display);

        return element switch
        {
            "Field" => (LastDotSegment(head), null, CodeMetricsMemberKind.Field),
            "Event" => (LastDotSegment(head), null, CodeMetricsMemberKind.Event),
            "Property" => (PropertyName(head), arity, CodeMetricsMemberKind.Property),
            _ => ParseMethodDisplay(head, arity, enclosingSimpleName),
        };
    }

    private static string PropertyName(string head)
    {
        var name = LastDotSegment(head);
        // Indexers display as `Type.this[int index]`; coverage spells them Item.
        return name.StartsWith("this[", StringComparison.Ordinal) ? "Item" : name;
    }

    private static (string MemberName, int? Arity, CodeMetricsMemberKind Kind) ParseMethodDisplay(
        string head, int? arity, string enclosingSimpleName)
    {
        var segment = LastDotSegment(head);
        if (TryAccessorName(head, segment, arity, out var accessor))
            return (accessor, null, CodeMetricsMemberKind.Accessor);

        if (TryOperatorName(head, arity, out var op))
            return (op, arity, CodeMetricsMemberKind.Method);

        // Top-level statements: Roslyn names the synthesized entry point `<Main>$` — normalized
        // to `Main`, the same identity MethodIdentity gives the coverage side, so the two match.
        // (StripGenericsPerSegment below would otherwise reduce it to `$`.)
        if (segment is "<Main>$")
            return ("Main", arity, CodeMetricsMemberKind.Method);

        segment = StripGenericsPerSegment(segment);

        // Constructor: no return type (the head IS the dotted name — no top-level space) and
        // the member name equals its type's simple name: `Calculator.Calculator(int seed)`.
        if (arity is not null && BalancedText.LastIndexOfTopLevel(head, ' ') < 0 && segment == enclosingSimpleName)
            return (".ctor", arity, CodeMetricsMemberKind.Method);

        return (segment, arity, CodeMetricsMemberKind.Method);
    }

    /// <summary>
    /// Accessor displays are paren-less and end in <c>.get</c> / <c>.set</c> / <c>.init</c> /
    /// <c>.add</c> / <c>.remove</c> — normalized to the coverage spelling (<c>get_Value</c>).
    /// </summary>
    private static bool TryAccessorName(string head, string segment, int? arity, out string name)
    {
        name = "";
        if (arity is not null || !AccessorPrefixes.TryGetValue(segment, out var prefix))
            return false;

        var owner = LastDotSegment(head[..(head.Length - segment.Length - 1)]);
        if (owner.StartsWith("this[", StringComparison.Ordinal)) owner = "Item";
        name = prefix + owner;
        return true;
    }

    /// <summary>Accessor display suffix → coverage-name prefix (mechanical mapping, so a table).</summary>
    private static readonly FrozenDictionary<string, string> AccessorPrefixes =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["get"] = "get_",
            ["set"] = "set_",
            ["init"] = "set_",   // init-only setters compile to set_ accessors
            ["add"] = "add_",
            ["remove"] = "remove_",
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>
    /// Conversion / user-defined operators: <c>Type.implicit operator int(...)</c>,
    /// <c>bool Type.operator ==(Type a, Type b)</c> → CLS <c>op_*</c> names.
    /// </summary>
    private static bool TryOperatorName(string head, int? arity, out string name)
    {
        name = "";
        if (FindTopLevel(head, ".implicit operator") >= 0) { name = "op_Implicit"; return true; }
        if (FindTopLevel(head, ".explicit operator") >= 0) { name = "op_Explicit"; return true; }

        var opIdx = FindTopLevel(head, ".operator ");
        if (opIdx < 0) return false;
        name = OperatorMethodName(head[(opIdx + ".operator ".Length)..].Trim(), arity);
        return true;
    }

    /// <summary>
    /// Split off the parameter list: head before the first top-level <c>(</c>, arity from
    /// counting top-level commas inside it. Depth-tracked (see <see cref="BalancedText"/>) so
    /// <c>Dictionary&lt;string, int&gt;</c> and <c>int[,]</c> never inflate arity.
    /// No parameter list → arity null.
    /// <para>
    /// Operator displays are located by their <c>.operator </c> marker instead: tokens like
    /// <c>operator &lt;</c> or <c>operator &gt;&gt;</c> carry unbalanced angle brackets that
    /// would derail depth tracking, and no operator token contains a parenthesis, so the
    /// parameter list is simply the first <c>(</c> after the marker.
    /// </para>
    /// </summary>
    private static (string Head, int? Arity) SplitParameterList(string display)
    {
        var opIdx = FindTopLevel(display, ".operator ");
        var open = opIdx >= 0
            ? display.IndexOf('(', opIdx)
            : BalancedText.IndexOfTopLevel(display, '(');
        if (open < 0) return (display, null);

        var close = display.LastIndexOf(')');
        var parameters = close > open ? display[(open + 1)..close] : display[(open + 1)..];
        return (display[..open], BalancedText.CountTopLevelItems(parameters));
    }

    private static int FindTopLevel(string head, string marker)
    {
        // Angle/bracket depth zero only — a marker inside generic arguments is not a member name.
        var depth = 0;
        for (var i = 0; i <= head.Length - marker.Length; i++)
        {
            var c = head[i];
            if (c is '<' or '[') depth++;
            else if (c is '>' or ']') depth--;
            else if (depth is 0 && head.AsSpan(i).StartsWith(marker, StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    private static string LastDotSegment(string head)
    {
        var start = BalancedText.LastIndexOfTopLevel(head, ' ') + 1;   // skip the return type, if any
        var lastDot = BalancedText.LastIndexOfTopLevel(head.AsSpan(start), '.');
        return lastDot < 0 ? head[start..] : head[(start + lastDot + 1)..];
    }

    /// <summary>
    /// C# operator token → CLS <c>op_*</c> method name: a mechanical mapping, so a table, not a
    /// switch. Unary/binary <c>+</c> and <c>-</c> share a token and are disambiguated by arity
    /// first. Unknown tokens keep a raw spelling so they land in the unmatched list with their
    /// display name rather than silently colliding.
    /// </summary>
    private static string OperatorMethodName(string token, int? arity)
    {
        if (arity is 1 && token is "+") return "op_UnaryPlus";
        if (arity is 1 && token is "-") return "op_UnaryNegation";
        return OperatorNames.TryGetValue(token, out var name) ? name : $"operator {token}";
    }

    private static readonly FrozenDictionary<string, string> OperatorNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["+"] = "op_Addition",
            ["-"] = "op_Subtraction",
            ["*"] = "op_Multiply",
            ["/"] = "op_Division",
            ["%"] = "op_Modulus",
            ["!"] = "op_LogicalNot",
            ["~"] = "op_OnesComplement",
            ["++"] = "op_Increment",
            ["--"] = "op_Decrement",
            ["true"] = "op_True",
            ["false"] = "op_False",
            ["&"] = "op_BitwiseAnd",
            ["|"] = "op_BitwiseOr",
            ["^"] = "op_ExclusiveOr",
            ["<<"] = "op_LeftShift",
            [">>"] = "op_RightShift",
            [">>>"] = "op_UnsignedRightShift",
            ["=="] = "op_Equality",
            ["!="] = "op_Inequality",
            ["<"] = "op_LessThan",
            [">"] = "op_GreaterThan",
            ["<="] = "op_LessThanOrEqual",
            [">="] = "op_GreaterThanOrEqual",
        }.ToFrozenDictionary(StringComparer.Ordinal);
}
