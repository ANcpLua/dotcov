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
        ["dynamic"] = "System.Object",
        ["nint"] = "System.IntPtr", ["nuint"] = "System.UIntPtr"
    };
    [GeneratedRegex(@"(?<![\w@.])\w+", RegexOptions.CultureInvariant)]
    private static partial Regex TypeWords();

    [GeneratedRegex(@"\bSystem\.(Boolean|Byte|SByte|Int16|UInt16|Int32|UInt32|Int64|UInt64|Char|Single|Double|Decimal|IntPtr|UIntPtr)\?", RegexOptions.CultureInvariant)]
    private static partial Regex NullableValueTypes();

    [GeneratedRegex(@"(System\.(?:String|Object)|\])\?", RegexOptions.CultureInvariant)]
    private static partial Regex NullableReferences();

    [GeneratedRegex(@"`[0-9]+(?=<)", RegexOptions.CultureInvariant)]
    private static partial Regex ConstructedArity();

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

    internal static void ValidateLength(string text, string identity)
    {
        if (text.Length > MaxSignatureLength)
            throw new InvalidDataException($"{identity} exceeds 16384 characters.");
    }

    internal static string? Coverage(string signature)
    {
        ValidateLength(signature, "Coverage signature");
        if (signature.Length < 2 || signature[0] is not '(' || signature[^1] is not ')') return null;
        return string.Join(",", BalancedText.SplitTopLevel(signature[1..^1]).Select(NormalizeType));
    }

    internal static string? Metrics(string display)
    {
        ValidateLength(display, "Metrics member display");
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
            var equals = BalancedText.LastIndexOfTopLevel(parameter, '=');
            var declaration = equals < 0 ? parameter : parameter[..equals].TrimEnd();
            var space = BalancedText.LastIndexOfTopLevel(declaration, ' ');
            if (space < 0) return null;
            types.Add(NormalizeType(declaration[..space]));
        }
        if (valueType is not null) types.Add(NormalizeType(valueType));
        return string.Join(",", types);
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
        type = type.Trim().Replace("global::", "", StringComparison.Ordinal);
        type = type.Replace('/', '.').Replace('+', '.');
        if (type.StartsWith("params ", StringComparison.Ordinal)) type = type[7..];
        var byRef = type.StartsWith("ref ", StringComparison.Ordinal) ||
                    type.StartsWith("out ", StringComparison.Ordinal) ||
                    type.StartsWith("in ", StringComparison.Ordinal);
        if (byRef) type = type[(type.IndexOf(' ') + 1)..];
        type = LowerTuples(type);
        type = TypeWords().Replace(type, static word => Aliases.GetValueOrDefault(word.Value, word.Value));
        type = NullableValueTypes().Replace(type, static match => $"System.Nullable`1<{match.Value[..^1]}>");
        type = NullableReferences().Replace(type, "$1");
        return ConstructedArity().Replace(type.Replace(" ", "", StringComparison.Ordinal), "") + (byRef ? "&" : "");
    }
}
