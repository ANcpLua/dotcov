using System.Text.RegularExpressions;

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

    [GeneratedRegex(@"`[0-9]+(?=<)", RegexOptions.CultureInvariant)]
    private static partial Regex ConstructedArity();

    [GeneratedRegex(@"[\p{L}_][\w.]*(?=<)", RegexOptions.CultureInvariant)]
    private static partial Regex GenericNames();

    internal static Dictionary<string, string?> GenericQualifications(IEnumerable<string?> coverage,
        IEnumerable<string?> metrics)
    {
        var names = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var signature in coverage) AddNames(signature, includeUnqualified: true);
        foreach (var signature in metrics) AddNames(signature, includeUnqualified: false);
        return names;

        void AddNames(string? signature, bool includeUnqualified)
        {
            if (signature is null) return;
            foreach (Match match in GenericNames().Matches(signature))
            {
                var name = match.Value;
                if (!includeUnqualified && !name.Contains('.')) continue;
                var simple = name[(name.LastIndexOf('.') + 1)..];
                if (!names.TryGetValue(simple, out var prior)) names.Add(simple, name);
                else if (prior != name) names[simple] = null;
            }
        }
    }

    internal static string? QualifyGenerics(string? signature, Dictionary<string, string?> names,
        bool fromMetrics = false)
    {
        if (signature is null) return null;
        if (fromMetrics && GenericNames().Matches(signature).Any(match =>
                names.TryGetValue(match.Value, out var name) && name is null)) return null;
        return GenericNames().Replace(signature, match => names.GetValueOrDefault(match.Value) ?? match.Value);
    }

    internal static string? Coverage(string signature)
    {
        if (signature.Length < 2 || signature[0] is not '(' || signature[^1] is not ')') return null;
        return string.Join(",", BalancedText.SplitTopLevel(signature[1..^1]).Select(NormalizeType));
    }

    internal static string? Metrics(string display)
    {
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

    private static string NormalizeType(string type)
    {
        type = type.Trim().Replace("global::", "", StringComparison.Ordinal);
        if (type.StartsWith("params ", StringComparison.Ordinal)) type = type[7..];
        var byRef = type.StartsWith("ref ", StringComparison.Ordinal) ||
                    type.StartsWith("out ", StringComparison.Ordinal) ||
                    type.StartsWith("in ", StringComparison.Ordinal);
        if (byRef) type = type[(type.IndexOf(' ') + 1)..];
        type = TypeWords().Replace(type, static word => Aliases.GetValueOrDefault(word.Value, word.Value));
        type = NullableValueTypes().Replace(type, static match => $"System.Nullable`1<{match.Value[..^1]}>");
        return ConstructedArity().Replace(type.Replace(" ", "", StringComparison.Ordinal), "") + (byRef ? "&" : "");
    }
}
