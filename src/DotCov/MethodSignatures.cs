using System.Text.RegularExpressions;
using System.Text;

namespace DotCov;

// Exact type spellings and C# built-in aliases can disambiguate overloads. Do not
// erase namespaces, generic arguments, array ranks, or by-ref markers to force a match.
internal static partial class MethodSignatures
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["bool"] = "System.Boolean", ["byte"] = "System.Byte", ["sbyte"] = "System.SByte",
        ["short"] = "System.Int16", ["ushort"] = "System.UInt16", ["int"] = "System.Int32",
        ["uint"] = "System.UInt32", ["long"] = "System.Int64", ["ulong"] = "System.UInt64",
        ["char"] = "System.Char", ["float"] = "System.Single", ["double"] = "System.Double",
        ["decimal"] = "System.Decimal", ["string"] = "System.String", ["object"] = "System.Object",
        ["dynamic"] = "System.Object", ["void"] = "System.Void",
        ["nint"] = "System.IntPtr", ["nuint"] = "System.UIntPtr"
    };
    private static readonly Dictionary<string, string> VisualBasicAliases = new(StringComparer.Ordinal)
    {
        ["Boolean"] = "System.Boolean", ["Byte"] = "System.Byte", ["SByte"] = "System.SByte",
        ["Short"] = "System.Int16", ["UShort"] = "System.UInt16", ["Integer"] = "System.Int32",
        ["UInteger"] = "System.UInt32", ["Long"] = "System.Int64", ["ULong"] = "System.UInt64",
        ["Char"] = "System.Char", ["Single"] = "System.Single", ["Double"] = "System.Double",
        ["Decimal"] = "System.Decimal", ["String"] = "System.String", ["Object"] = "System.Object",
        ["Date"] = "System.DateTime"
    };
    [GeneratedRegex(@"(?<![\w@.])\w+", RegexOptions.CultureInvariant)]
    private static partial Regex TypeWords();

    // Visual Basic: [modifiers] name As Type.
    [GeneratedRegex(@"^((?:(?:ByRef|ByVal|Optional|ParamArray)\s+)*)[\p{L}_]\w*\s+As\s+(.+)$",
        RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex VisualBasicParameter();

    [GeneratedRegex(@"\bSystem\.(Boolean|Byte|SByte|Int16|UInt16|Int32|UInt32|Int64|UInt64|Char|Single|Double|Decimal|IntPtr|UIntPtr)\?", RegexOptions.CultureInvariant)]
    private static partial Regex NullableValueTypes();

    [GeneratedRegex(@"(System\.(?:String|Object)|\])\?", RegexOptions.CultureInvariant)]
    private static partial Regex NullableReferences();

    [GeneratedRegex(@"System\.Nullable<([\p{L}_][\w.]*)", RegexOptions.CultureInvariant)]
    private static partial Regex NullableTypeNames();

    [GeneratedRegex(@"`[0-9]+(?=<)", RegexOptions.CultureInvariant)]
    private static partial Regex ConstructedArity();

    // delegate*<P1, ..., R>, with or without a calling convention; the last '>' closes it.
    [GeneratedRegex(@"^delegate\*\s*(?:managed|unmanaged(?:\[[^\]]*\])?)?\s*<(.*)>([^>]*)$",
        RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex FunctionPointerType();

    private const int MaxSignatureLength = 16_384;
    // Tokenize with the linear engine; no delimiter lookahead can force retries
    // over every suffix of a long, untrusted identifier.
    private static readonly Regex TypeNamePattern = new(@"[\p{L}_][\w.]*",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static Dictionary<string, string?> TypeQualifications(IEnumerable<string?> coverage,
        IEnumerable<string?> metrics)
    {
        var names = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var signature in coverage) AddNames(signature, includeUnqualified: true);
        foreach (var signature in metrics) AddNames(signature, includeUnqualified: false);
        return names;

        void AddNames(string? signature, bool includeUnqualified)
        {
            if (signature is null) return;
            foreach (Match match in TypeNamePattern.Matches(signature))
            {
                var name = match.Value;
                if (!includeUnqualified && !name.Contains('.')) continue;
                var simple = name[(name.LastIndexOf('.') + 1)..];
                if (!names.TryGetValue(simple, out var prior)) names.Add(simple, name);
                else if (prior != name && (includeUnqualified || prior is null ||
                         !prior.EndsWith("." + name, StringComparison.Ordinal))) names[simple] = null;
            }
        }
    }

    internal static string? QualifyTypes(string? signature, Dictionary<string, string?> names,
        bool fromMetrics = false)
    {
        if (signature is null) return null;
        if (fromMetrics && TypeNamePattern.Matches(signature).Any(match =>
                names.TryGetValue(match.Value, out var name) && name is null)) return null;
        return TypeNamePattern.Replace(signature, match =>
        {
            var value = match.Value;
            var full = names.GetValueOrDefault(value[(value.LastIndexOf('.') + 1)..]);
            return full is not null && (full == value || full.EndsWith("." + value, StringComparison.Ordinal))
                ? full : value;
        });
    }

    /// <summary>
    /// Visual Basic displays name the member kind, end a function with its return type, spell
    /// type parameters <c>(Of T)</c> and name constructors <c>New</c>:
    /// <c>Function A.M(Of T)(x As T) As Long</c> becomes <c>A.M&lt;T&gt;(x As T)</c> and
    /// <c>Sub A.New()</c> becomes <c>A.A()</c>, the C# shape every later step reads.
    /// </summary>
    internal static string VisualBasicDisplay(string display)
    {
        var function = display.StartsWith("Function ", StringComparison.Ordinal);
        if (!function && !display.StartsWith("Sub ", StringComparison.Ordinal)) return display;
        var end = display.LastIndexOf(") As ", StringComparison.Ordinal);
        display = function && end >= 0 ? display[9..(end + 1)] : display[(display.IndexOf(' ') + 1)..];
        display = VisualBasicTypeParameters().Replace(display, "<$1>");
        return VisualBasicConstructor().Replace(display, "$1.$1(");
    }

    [GeneratedRegex(@"\(Of ([^()]*)\)(?=[.(])", RegexOptions.CultureInvariant)]
    private static partial Regex VisualBasicTypeParameters();

    [GeneratedRegex(@"([^.\s(<>]+)\.New\(", RegexOptions.CultureInvariant)]
    private static partial Regex VisualBasicConstructor();

    internal static void ValidateLength(string text, string identity)
    {
        if (text.Length > MaxSignatureLength)
            throw new InvalidDataException($"{identity} exceeds 16384 characters.");
    }

    internal sealed class NullableIdentities(IEnumerable<string?> coverage)
    {
        public readonly HashSet<string> Observed = coverage.Where(static s => s is not null)
            .SelectMany(static s => TypeNamePattern.Matches(s!).Select(static m => m.Value)).ToHashSet(StringComparer.Ordinal);
        public readonly HashSet<string> Values = coverage.Where(static s => s is not null)
            .SelectMany(static s => NullableTypeNames().Matches(s!).Select(static m => m.Groups[1].Value)).ToHashSet(StringComparer.Ordinal);
        // Names some coverage signature carries without a Nullable wrapper.
        public readonly HashSet<string> Bare = coverage.Where(static s => s is not null)
            .SelectMany(static s => BareNames(s!)).ToHashSet(StringComparer.Ordinal);

        private static IEnumerable<string> BareNames(string signature)
        {
            var wrapped = NullableTypeNames().Matches(signature).Select(static m => m.Groups[1].Value).ToList();
            foreach (Match match in TypeNamePattern.Matches(signature))
                if (!wrapped.Remove(match.Value)) yield return match.Value;
        }
    }

    internal static string? ResolveNullable(string? signature, NullableIdentities identities)
    {
        if (signature is null || !signature.Contains('?')) return signature;
        var parents = new Stack<int>();
        var nullableStarts = new HashSet<int>();
        var replacements = new Dictionary<int, char>();
        var start = 0;
        for (var i = 0; i < signature.Length; i++)
        {
            var c = signature[i];
            if (c == '?')
            {
                var name = start;
                while (name < i && (char.IsLetterOrDigit(signature[name]) || signature[name] is '_' or '.')) name++;
                var type = signature[start..name];
                if (identities.Values.Contains(type))
                {
                    // A generic parameter is a value type in one method and a reference type in
                    // another: the report-wide sets cannot tell which T this is.
                    if (!type.Contains('.') && identities.Bare.Contains(type)) return null;
                    if (!nullableStarts.Add(start)) return signature;
                    replacements.Add(i, '>');
                }
                else if (identities.Observed.Contains(type)) replacements.Add(i, '\0');
                continue;
            }
            if (c == '<')
            {
                parents.Push(start);
                start = i + 1;
            }
            else if (c == '>' && !parents.TryPop(out start)) return signature;
            else if (c == ',') start = i + 1;
        }
        var output = new StringBuilder(signature.Length);
        for (var i = 0; i < signature.Length; i++)
        {
            if (nullableStarts.Contains(i)) output.Append("System.Nullable<");
            var c = replacements.GetValueOrDefault(i, signature[i]);
            if (c is not '\0') output.Append(c);
        }
        return output.ToString();
    }

    internal static string? Coverage(string signature)
    {
        ValidateLength(signature, "Coverage signature");
        if (signature.Length < 2 || signature[0] is not '(' || signature[^1] is not ')') return null;
        // IL spells tuples as ValueTuple`N, so an inner parenthesis is a function pointer, which
        // Coverlet truncates at its last '(' ("(System.Int32))"): unknown, not a conflict.
        if (signature.AsSpan(1, signature.Length - 2).IndexOfAny('(', ')') >= 0) return null;
        return string.Join(",", BalancedText.SplitTopLevel(signature[1..^1]).Select(NormalizeType));
    }

    internal static string? Metrics(string display)
    {
        ValidateLength(display, "Metrics member display");
        display = VisualBasicDisplay(display);
        string parameters;
        string? valueType = null;
        if (display.EndsWith(')'))
        {
            var open = CodeMetricsReader.ParameterListOpen(display);
            if (open < 0) return null;
            parameters = display[(open + 1)..^1];
        }
        else
        {
            var indexer = display.IndexOf(".this[", StringComparison.Ordinal);
            var close = display.LastIndexOf(']');
            if (indexer < 0 || close < indexer) return null;
            var accessor = display[(close + 1)..];
            if (accessor is not (".get" or ".set" or ".init")) return null;
            parameters = display[(indexer + 6)..close];
            if (accessor is not ".get")
            {
                var space = BalancedText.LastIndexOfTopLevel(display.AsSpan(0, indexer), ' ');
                if (space < 0) return null;
                valueType = display[..space];
                if (valueType is "void") return null;
            }
        }
        var types = new List<string>();
        foreach (var parameter in BalancedText.SplitTopLevel(parameters))
        {
            var unbracketed = parameter.StartsWith('[') ? parameter[1..^1] : parameter;
            var equals = BalancedText.LastIndexOfTopLevel(unbracketed, '=');
            var declaration = equals < 0 ? unbracketed : unbracketed[..equals].TrimEnd();
            var visualBasic = VisualBasicParameter().Match(declaration);
            if (visualBasic.Success)
            {
                if (VisualBasicType(visualBasic.Groups[2].Value) is not { } type) return null;
                types.Add(NormalizeType((visualBasic.Groups[1].Value.Contains("ByRef") ? "ref " : "") + type));
                continue;
            }
            var space = BalancedText.LastIndexOfTopLevel(declaration, ' ');
            if (space < 0) return null;
            types.Add(NormalizeType(declaration[..space]));
        }
        if (valueType is not null) types.Add(NormalizeType(valueType));
        return string.Join(",", types);
    }

    // Visual Basic spells generics List(Of T) and arrays T() or T(,). Its tuples, a parenthesis
    // at the start or after a space, stay unknown.
    private static string? VisualBasicType(string type)
    {
        var output = new StringBuilder(type.Length);
        var closers = new Stack<char>();
        for (var i = 0; i < type.Length; i++)
        {
            switch (type[i])
            {
                case '(' when type.AsSpan(i + 1).StartsWith("Of "):
                    output.Append('<');
                    closers.Push('>');
                    i += 3;
                    break;
                case '(' when i > 0 && type[i - 1] != ' ':
                    output.Append('[');
                    closers.Push(']');
                    break;
                case '(':
                    return null;
                case ')':
                    output.Append(closers.TryPop(out var close) ? close : ')');
                    break;
                default:
                    output.Append(type[i]);
                    break;
            }
        }
        return TypeWords().Replace(output.ToString(), static word => VisualBasicAliases.GetValueOrDefault(word.Value, word.Value));
    }

    private sealed class TupleFrame(int angle, int array, int start, bool nullable)
    {
        public readonly int Angle = angle;
        public readonly int Array = array;
        public readonly bool Nullable = nullable;
        public int ElementStart = start;
        public int Items = 1;
        public int RestDepth;
    }

    private static string LowerTuples(string type)
    {
        var closes = new Dictionary<int, int>();
        var opens = new Stack<int>();
        for (var i = 0; i < type.Length; i++)
        {
            if (type[i] == '(')
            {
                if (opens.Count == 64) throw new InvalidDataException("Tuple nesting exceeds 64 levels.");
                opens.Push(i);
            }
            else if (type[i] == ')')
            {
                if (!opens.TryPop(out var open)) throw new InvalidDataException("Unbalanced tuple type.");
                closes.Add(open, i);
            }
        }
        if (opens.Count is not 0) throw new InvalidDataException("Unbalanced tuple type.");
        var output = new StringBuilder(type.Length);
        var frames = new Stack<TupleFrame>();
        var angle = 0;
        var array = 0;
        for (var i = 0; i < type.Length; i++)
        {
            var c = type[i];
            if (c == '(')
            {
                var close = closes[i];
                var nullable = close + 1 < type.Length && type[close + 1] == '?';
                if (nullable) output.Append("System.Nullable<");
                output.Append("System.ValueTuple<");
                frames.Push(new TupleFrame(angle, array, output.Length, nullable));
            }
            else if (c == ')')
            {
                var frame = frames.Pop();
                TrimTupleName(output, frame.ElementStart);
                output.Append('>', frame.RestDepth + 1);
                if (frame.Nullable)
                {
                    output.Append('>');
                    i++;
                }
            }
            else if (c == ',' && frames.TryPeek(out var frame) && frame.Angle == angle && frame.Array == array)
            {
                TrimTupleName(output, frame.ElementStart);
                output.Append(',');
                if (frame.Items++ % 7 is 0)
                {
                    output.Append("System.ValueTuple<");
                    frame.RestDepth++;
                }
                frame.ElementStart = output.Length;
            }
            else
            {
                output.Append(c);
                switch (c)
                {
                    case '<': angle++; break;
                    case '>': angle--; break;
                    case '[': array++; break;
                    case ']': array--; break;
                }
            }
        }
        return output.ToString();
    }

    private static void TrimTupleName(StringBuilder output, int start)
    {
        var end = output.Length;
        while (end > start && char.IsWhiteSpace(output[end - 1])) end--;
        if (end == start) throw new InvalidDataException("Tuple element is empty.");
        var word = end;
        while (word > start && (char.IsLetterOrDigit(output[word - 1]) || output[word - 1] is '_' or '@')) word--;
        if (word == end || word == start || !char.IsWhiteSpace(output[word - 1])) return;
        var prefix = word;
        while (prefix > start && char.IsWhiteSpace(output[prefix - 1])) prefix--;
        if (prefix > start) output.Length = prefix;
    }

    private static string NormalizeType(string type)
    {
        type = MethodIdentity.SourceTypeName(type.Trim().Replace("global::", "", StringComparison.Ordinal));
        type = type.Replace('/', '.').Replace('+', '.');
        if (type.StartsWith("params ", StringComparison.Ordinal)) type = type[7..];
        var byRef = type.StartsWith("ref ", StringComparison.Ordinal) ||
                    type.StartsWith("out ", StringComparison.Ordinal) ||
                    type.StartsWith("in ", StringComparison.Ordinal);
        if (byRef) type = type[(type.IndexOf(' ') + 1)..];
        if (byRef && type.StartsWith("readonly ", StringComparison.Ordinal)) type = type[9..];
        // One canonical function-pointer identity without its calling convention: it conflicts
        // with every other parameter type, while Coverlet's truncated spelling stays unknown.
        var pointer = FunctionPointerType().Match(type);
        if (pointer.Success)
            type = $"delegate*<{string.Join(",", BalancedText.SplitTopLevel(pointer.Groups[1].Value).Select(NormalizeType))}>{pointer.Groups[2].Value}";
        type = LowerTuples(type);
        type = TypeWords().Replace(type, static word => Aliases.GetValueOrDefault(word.Value, word.Value));
        // @int names a type called int, never the alias: drop the escape once aliases are applied.
        type = type.Replace("@", "", StringComparison.Ordinal);
        type = NullableValueTypes().Replace(type, static match => $"System.Nullable`1<{match.Value[..^1]}>");
        type = NullableReferences().Replace(type, "$1");
        return ConstructedArity().Replace(type.Replace(" ", "", StringComparison.Ordinal), "") + (byRef ? "&" : "");
    }
}
