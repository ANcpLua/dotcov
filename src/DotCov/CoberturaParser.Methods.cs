using System.Collections.ObjectModel;
using System.Globalization;
using System.Xml;

namespace DotCov;

// ── Opt-in method-level parse ────────────────────────────────────────────────
//
// The class-level parse (ParseCore) deliberately DEDUPES <methods><method><lines> into
// per-file line sets — that is the published merge semantics and it must not change.
// The CRAP gate needs the opposite: every <method> kept distinct, with its own line set.
// This partial adds that as a separate additive API instead of an option on Parse, so the
// existing entry points keep their exact behavior and their return type.
public static partial class CoberturaParser
{
    /// <summary>
    /// Parse per-method coverage detail from a Cobertura document. Opt-in and additive:
    /// <see cref="Parse"/> and friends are untouched. Entries are RAW per-method records —
    /// one per distinct (file, class, method name, signature) — including compiler-synthesized
    /// classes (state machines, lambda display classes); interpretation belongs to
    /// <see cref="CrapAnalysis"/>. Reports without <c>&lt;methods&gt;</c> detail (the original
    /// Cobertura summary shape, MTP's emitter) produce an empty list, never a throw.
    /// </summary>
    public static IReadOnlyList<MethodCoverage> ParseMethods(Stream stream, long maxChars = DefaultMaxChars)
    {
        using var reader = XmlReader.Create(stream, CreateSecureSettings(maxChars));
        var methods = new Dictionary<string, MethodAccumulator>(StringComparer.Ordinal);
        var order = new List<MethodAccumulator>();
        CollectMethods(reader, methods, order);
        return MaterializeMethods(order);
    }

    /// <summary>
    /// <see cref="ParseMethods"/> from a file path, with the same path-prefixing
    /// <see cref="XmlException"/> rethrow contract as <see cref="ParseFile"/> so directory
    /// aggregates name the malformed report.
    /// </summary>
    public static IReadOnlyList<MethodCoverage> ParseMethodsFile(string path, long maxChars = DefaultMaxChars)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return ParseMethods(stream, maxChars);
        }
        catch (XmlException ex)
        {
            // Same rethrow shape as ParseFile — see the rationale there.
            throw new XmlException(
                $"{path}: {LocationSentencePattern().Replace(ex.Message, "")}",
                ex, ex.LineNumber, ex.LinePosition);
        }
    }

    /// <summary>
    /// Parse and merge per-method detail from every matching report under
    /// <paramref name="directory"/> — same pattern contract as <see cref="ParseDirectory(string, string)"/>
    /// (they share the single pattern gate). The same method entry across files merges per line
    /// with <c>Math.Max</c>, mirroring the class-level union-with-max semantics.
    /// </summary>
    public static IReadOnlyList<MethodCoverage> ParseMethodsDirectory(string directory, string pattern = DefaultPattern) =>
        ParseMethodsDirectory(directory, pattern, DefaultMaxChars);

    /// <summary><see cref="ParseMethodsDirectory(string, string)"/> with an explicit per-file character cap.</summary>
    public static IReadOnlyList<MethodCoverage> ParseMethodsDirectory(string directory, string pattern, long maxChars)
    {
        var files = FindReports(directory, pattern);
        if (files.Length is 0) return [];

        var methods = new Dictionary<string, MethodAccumulator>(StringComparer.Ordinal);
        var order = new List<MethodAccumulator>();

        foreach (var file in files.OrderBy(static f => f, StringComparer.Ordinal))
        {
            try
            {
                using var stream = File.OpenRead(file);
                using var reader = XmlReader.Create(stream, CreateSecureSettings(maxChars));
                CollectMethods(reader, methods, order);
            }
            catch (XmlException ex)
            {
                throw new XmlException(
                    $"{file}: {LocationSentencePattern().Replace(ex.Message, "")}",
                    ex, ex.LineNumber, ex.LinePosition);
            }
        }

        return MaterializeMethods(order);
    }

    /// <summary>File-or-directory dispatch for method-level detail, mirroring <see cref="ParsePath(string)"/>.</summary>
    public static IReadOnlyList<MethodCoverage> ParseMethodsPath(string path) => ParseMethodsPath(path, DefaultMaxChars);

    /// <summary><see cref="ParseMethodsPath(string)"/> with an explicit per-file character cap.</summary>
    public static IReadOnlyList<MethodCoverage> ParseMethodsPath(string path, long maxChars)
    {
        if (File.Exists(path))
            return ParseMethodsFile(path, maxChars);
        if (Directory.Exists(path))
            return ParseMethodsDirectory(path, DefaultPattern, maxChars);

        throw new FileNotFoundException($"No file or directory at '{path}'.");
    }

    private sealed class MethodAccumulator(string className, string methodName, string signature, string file)
    {
        public readonly string ClassName = className;
        public readonly string MethodName = methodName;
        public readonly string Signature = signature;
        public readonly string File = file;
        public readonly Dictionary<int, int> LineHits = new();
        public int? Complexity;
    }

    private static void CollectMethods(
        XmlReader reader, Dictionary<string, MethodAccumulator> methods, List<MethodAccumulator> order)
    {
        // Source roots are per-document state; the warnings ConsumeSource can emit have no
        // channel on this API (a method list is not a report) and are re-observable through
        // the class-level parse of the same document, so they are deliberately discarded.
        var sourceRoots = new List<string>();
        var discardedWarnings = new List<CoverageWarning>();

        while (reader.Read())
        {
            if (reader is { NodeType: XmlNodeType.Element, LocalName: "source" })
            {
                ConsumeSource(reader, sourceRoots, discardedWarnings);
                continue;
            }

            if (reader is not { NodeType: XmlNodeType.Element, LocalName: "class" })
                continue;

            ConsumeClassMethods(reader, methods, order, sourceRoots);
        }
    }

    private static void ConsumeClassMethods(
        XmlReader reader,
        Dictionary<string, MethodAccumulator> methods,
        List<MethodAccumulator> order,
        List<string> sourceRoots)
    {
        var filename = reader.GetAttribute("filename");
        if (filename is null) return;

        // Identical file-key normalization to ConsumeClass, so method entries join against
        // FileCoverage.Path on the same key.
        filename = ResolveFileKey(filename.Replace('\\', '/'), sourceRoots);
        var className = reader.GetAttribute("name") ?? "";

        using var sub = reader.ReadSubtree();
        sub.MoveToContent();

        while (sub.Read())
        {
            // Only <method> subtrees contribute — the class-level trailing <lines> summary is
            // exactly what this API exists NOT to fold in, and skipping non-method elements
            // here is what keeps it out (its <line> children are never visited).
            if (sub is not { NodeType: XmlNodeType.Element, LocalName: "method" })
                continue;

            var key = $"{filename}\n{className}\n{sub.GetAttribute("name")}\n{sub.GetAttribute("signature")}";
            if (!methods.TryGetValue(key, out var acc))
            {
                acc = new MethodAccumulator(
                    className, sub.GetAttribute("name") ?? "", sub.GetAttribute("signature") ?? "", filename);
                methods[key] = acc;
                order.Add(acc);
            }

            if (ParseUsableComplexity(sub.GetAttribute("complexity")) is { } complexity)
                acc.Complexity = acc.Complexity is { } existing ? Math.Max(existing, complexity) : complexity;

            using var lines = sub.ReadSubtree();
            lines.MoveToContent();
            while (lines.Read())
            {
                if (lines is not { NodeType: XmlNodeType.Element, LocalName: "line" })
                    continue;

                if (!int.TryParse(lines.GetAttribute("number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lineNum))
                    continue;

                // Same parse-as-long-and-saturate policy as ConsumeClass; unparseable hits
                // degrade to 0 (this API has no warnings channel — the class-level parse of
                // the same document surfaces the MalformedHits warning).
                var hits = 0;
                if (lines.GetAttribute("hits") is { } hitsAttr &&
                    long.TryParse(hitsAttr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var h))
                    hits = (int)Math.Clamp(h, int.MinValue, int.MaxValue);

                acc.LineHits[lineNum] = acc.LineHits.TryGetValue(lineNum, out var prev)
                    ? Math.Max(prev, hits)
                    : hits;
            }
        }
    }

    /// <summary>
    /// A method-level <c>complexity</c> attribute, admitted only when it is a real measurement:
    /// coverlet emits integer cyclomatic complexity, but gcovr/grcov/cover2cover emit a
    /// placeholder <c>0</c>/<c>0.0</c> and ReportGenerator merges can produce <c>NaN</c>.
    /// Cyclomatic complexity is ≥ 1 by construction, so anything below 1 — including NaN,
    /// which fails every comparison — is "not measured", never a measurement of zero.
    /// </summary>
    private static int? ParseUsableComplexity(string? attr)
    {
        if (attr is null) return null;
        if (!double.TryParse(attr, NumberStyles.Float, CultureInfo.InvariantCulture, out var c)) return null;
        if (!(c >= 1) || c > int.MaxValue) return null;
        return (int)Math.Round(c, MidpointRounding.AwayFromZero);
    }

    private static IReadOnlyList<MethodCoverage> MaterializeMethods(List<MethodAccumulator> order)
    {
        var result = new List<MethodCoverage>(order.Count);
        foreach (var acc in order)
        {
            var linesHit = 0;
            var start = 0;
            var end = 0;
            foreach (var (line, hits) in acc.LineHits)
            {
                if (hits > 0) linesHit++;
                if (start is 0 || line < start) start = line;
                if (line > end) end = line;
            }

            result.Add(new MethodCoverage(
                acc.ClassName, acc.MethodName, acc.Signature, acc.File,
                start, end, linesHit, acc.LineHits.Count, acc.Complexity)
            {
                LineHits = new ReadOnlyDictionary<int, int>(acc.LineHits)
            });
        }

        return result;
    }
}
