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
        ["nint"] = "System.IntPtr", ["nuint"] = "System.UIntPtr"
    };
    [GeneratedRegex(@"(?<![\w@.])\w+", RegexOptions.CultureInvariant)]
    private static partial Regex TypeWords();

    internal static string? Coverage(string signature)
    {
        if (signature.Length < 2 || signature[0] is not '(' || signature[^1] is not ')') return null;
        return string.Join(",", BalancedText.SplitTopLevel(signature[1..^1]).Select(NormalizeType));
    }

    internal static string? Metrics(string display)
    {
        if (!display.EndsWith(')')) return null;
        var open = BalancedText.LastIndexOfTopLevel(display, '(');
        if (open < 0) return null;
        var types = new List<string>();
        foreach (var parameter in BalancedText.SplitTopLevel(display[(open + 1)..^1]))
        {
            var equals = BalancedText.LastIndexOfTopLevel(parameter, '=');
            var declaration = equals < 0 ? parameter : parameter[..equals].TrimEnd();
            var space = BalancedText.LastIndexOfTopLevel(declaration, ' ');
            if (space < 0) return null;
            types.Add(NormalizeType(declaration[..space]));
        }
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
        return type.Replace(" ", "", StringComparison.Ordinal) + (byRef ? "&" : "");
    }
}
