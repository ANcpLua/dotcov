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

    private static IReadOnlyList<CodeMetricsMember> ParseCore(XmlReader reader)
    {
        var members = new List<CodeMetricsMember>();
        var ns = "";
        var typeChain = new List<string>();     // generic-stripped dotted segments, e.g. "Outer.Inner"
        var memberStack = new Stack<PendingMember>();  // Property → its Accessor methods nest

        while (reader.Read())
        {
            if (reader.NodeType is XmlNodeType.Element)
            {
                var isEmpty = reader.IsEmptyElement;
                switch (reader.LocalName)
                {
                    case "Namespace":
                        ns = reader.GetAttribute("Name") ?? "";
                        break;

                    case "NamedType":
                        // Empty elements raise no EndElement, so only a real subtree pushes.
                        if (!isEmpty) typeChain.Add(StripGenericsPerSegment(reader.GetAttribute("Name") ?? ""));
                        break;

                    case "Method" or "Field" or "Property" or "Event" when typeChain.Count > 0:
                        // An empty member element carries no Metrics — nothing to record.
                        if (!isEmpty)
                            memberStack.Push(new PendingMember(reader.GetAttribute("Name") ?? "", reader.LocalName));
                        break;

                    case "Metric" when memberStack.Count > 0 &&
                                       reader.GetAttribute("Name") is "CyclomaticComplexity" &&
                                       int.TryParse(reader.GetAttribute("Value"), NumberStyles.Integer,
                                           CultureInfo.InvariantCulture, out var value):
                        // Assign to the innermost OPEN member: a Property's own Metrics arrive
                        // before its <Accessors>, so accessor metrics can never leak upward.
                        memberStack.Peek().Complexity = value;
                        break;
                }
            }
            else if (reader.NodeType is XmlNodeType.EndElement)
            {
                switch (reader.LocalName)
                {
                    case "Namespace":
                        ns = "";
                        break;

                    case "NamedType":
                        if (typeChain.Count > 0) typeChain.RemoveAt(typeChain.Count - 1);
                        break;

                    case "Method" or "Field" or "Property" or "Event" when memberStack.Count > 0:
                        var pending = memberStack.Pop();
                        // A member without a CyclomaticComplexity metric has nothing to
                        // contribute to a complexity table — skipped, not zeroed.
                        if (pending.Complexity is { } complexity)
                            members.Add(Materialize(pending, ns, typeChain, complexity));
                        break;
                }
            }
        }

        return members;
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

        if (element is "Field")
            return (LastDotSegment(head), null, CodeMetricsMemberKind.Field);

        if (element is "Event")
            return (LastDotSegment(head), null, CodeMetricsMemberKind.Event);

        if (element is "Property")
        {
            var name = LastDotSegment(head);
            // Indexers display as `Type.this[int index]`; coverage spells them Item.
            if (name.StartsWith("this[", StringComparison.Ordinal)) name = "Item";
            return (name, arity, CodeMetricsMemberKind.Property);
        }

        // Method element. Accessor displays are paren-less and end in `.get` / `.set` / …
        var segment = LastDotSegment(head);
        if (arity is null && segment is "get" or "set" or "init" or "add" or "remove")
        {
            var owner = LastDotSegment(head[..(head.Length - segment.Length - 1)]);
            if (owner.StartsWith("this[", StringComparison.Ordinal)) owner = "Item";
            var prefix = segment switch
            {
                "get" => "get_",
                "set" or "init" => "set_",   // init-only setters compile to set_ accessors
                "add" => "add_",
                _ => "remove_",
            };
            return (prefix + owner, null, CodeMetricsMemberKind.Accessor);
        }

        // Conversion / user-defined operators: `Type.implicit operator int(...)`,
        // `bool Type.operator ==(Type a, Type b)`.
        if (FindTopLevel(head, ".implicit operator") >= 0)
            return ("op_Implicit", arity, CodeMetricsMemberKind.Method);
        if (FindTopLevel(head, ".explicit operator") >= 0)
            return ("op_Explicit", arity, CodeMetricsMemberKind.Method);
        var opIdx = FindTopLevel(head, ".operator ");
        if (opIdx >= 0)
            return (OperatorMethodName(head[(opIdx + ".operator ".Length)..].Trim(), arity), arity,
                CodeMetricsMemberKind.Method);

        segment = StripGenericsPerSegment(segment);

        // Constructor: no return type (the head IS the dotted name — no top-level space) and
        // the member name equals its type's simple name: `Calculator.Calculator(int seed)`.
        if (arity is not null && FindTopLevelSpace(head) < 0 && segment == enclosingSimpleName)
            return (".ctor", arity, CodeMetricsMemberKind.Method);

        return (segment, arity, CodeMetricsMemberKind.Method);
    }

    /// <summary>
    /// Split off the parameter list: head before the first top-level <c>(</c>, arity from
    /// counting top-level commas inside it. Depth-tracked over <c>&lt;&gt;</c>, <c>[]</c>, and
    /// <c>()</c> so <c>Dictionary&lt;string, int&gt;</c> and <c>int[,]</c> never inflate arity.
    /// No parameter list → arity null.
    /// </summary>
    private static (string Head, int? Arity) SplitParameterList(string display)
    {
        var depth = 0;
        for (var i = 0; i < display.Length; i++)
        {
            var c = display[i];
            if (c is '<' or '[') depth++;
            else if (c is '>' or ']') depth--;
            else if (c is '(' && depth is 0)
            {
                var close = display.LastIndexOf(')');
                var parameters = close > i ? display[(i + 1)..close] : display[(i + 1)..];
                return (display[..i], CountParameters(parameters));
            }
        }
        return (display, null);
    }

    private static int CountParameters(string parameters)
    {
        if (parameters.AsSpan().Trim().Length is 0) return 0;
        var depth = 0;
        var count = 1;
        foreach (var c in parameters)
        {
            if (c is '<' or '[' or '(') depth++;
            else if (c is '>' or ']' or ')') depth--;
            else if (c is ',' && depth is 0) count++;
        }
        return count;
    }

    /// <summary>Last top-level space (outside <c>&lt;&gt;</c>/<c>[]</c>), or -1: separates return type from the dotted name.</summary>
    private static int FindTopLevelSpace(string head)
    {
        var depth = 0;
        var last = -1;
        for (var i = 0; i < head.Length; i++)
        {
            var c = head[i];
            if (c is '<' or '[') depth++;
            else if (c is '>' or ']') depth--;
            else if (c is ' ' && depth is 0) last = i;
        }
        return last;
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
        var start = FindTopLevelSpace(head) + 1;   // skip the return type, if any
        var depth = 0;
        var lastDot = -1;
        for (var i = start; i < head.Length; i++)
        {
            var c = head[i];
            if (c is '<' or '[') depth++;
            else if (c is '>' or ']') depth--;
            else if (c is '.' && depth is 0) lastDot = i;
        }
        return lastDot < 0 ? head[start..] : head[(lastDot + 1)..];
    }

    /// <summary>
    /// C# operator token → CLS <c>op_*</c> method name, disambiguating unary/binary <c>+</c> and
    /// <c>-</c> by arity. Unknown tokens keep a raw spelling so they land in the unmatched list
    /// with their display name rather than silently colliding.
    /// </summary>
    private static string OperatorMethodName(string token, int? arity) => token switch
    {
        "+" => arity is 1 ? "op_UnaryPlus" : "op_Addition",
        "-" => arity is 1 ? "op_UnaryNegation" : "op_Subtraction",
        "*" => "op_Multiply",
        "/" => "op_Division",
        "%" => "op_Modulus",
        "!" => "op_LogicalNot",
        "~" => "op_OnesComplement",
        "++" => "op_Increment",
        "--" => "op_Decrement",
        "true" => "op_True",
        "false" => "op_False",
        "&" => "op_BitwiseAnd",
        "|" => "op_BitwiseOr",
        "^" => "op_ExclusiveOr",
        "<<" => "op_LeftShift",
        ">>" => "op_RightShift",
        ">>>" => "op_UnsignedRightShift",
        "==" => "op_Equality",
        "!=" => "op_Inequality",
        "<" => "op_LessThan",
        ">" => "op_GreaterThan",
        "<=" => "op_LessThanOrEqual",
        ">=" => "op_GreaterThanOrEqual",
        _ => $"operator {token}",
    };
}
