using System.Collections.ObjectModel;
using System.Xml;

namespace DotCov;

/// <summary>Identity of one Cobertura <c>&lt;method&gt;</c> entry within a document set.</summary>
internal readonly record struct MethodKey(string File, string ClassName, string MethodName, string Signature);

public static partial class CoberturaParser
{
    /// <summary>
    /// Parse per-method coverage detail from a Cobertura document. Entries are RAW per-method
    /// records — one per distinct (file, class, method name, signature) — including
    /// compiler-synthesized classes (state machines, lambda display classes); interpretation
    /// belongs to <see cref="CrapAnalysis"/>. Reports without <c>&lt;methods&gt;</c> detail
    /// (the original Cobertura summary shape, MTP's emitter) produce an empty list, never a throw.
    /// </summary>
    public static IReadOnlyList<MethodCoverage> ParseMethods(Stream stream, long maxChars = DefaultMaxChars)
    {
        using var reader = CreateReader(stream, maxChars, async: false);
        var methods = new MethodCollector();
        CollectMethods(reader, methods);
        return methods.Materialize();
    }

    public static IReadOnlyList<MethodCoverage> ParseMethodsFile(string path, long maxChars = DefaultMaxChars)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return ParseMethods(stream, maxChars);
        }
        catch (XmlException ex)
        {
            throw new XmlException(
                $"{path}: {LocationSentencePattern().Replace(ex.Message, "")}",
                ex, ex.LineNumber, ex.LinePosition);
        }
    }

    /// <summary>
    /// Parse and merge per-method detail from every matching report under
    /// <paramref name="directory"/>. The same method entry across files merges per line with
    /// <c>Math.Max</c>, mirroring the file-level union-with-max semantics.
    /// </summary>
    public static IReadOnlyList<MethodCoverage> ParseMethodsDirectory(string directory, string pattern = DefaultPattern) =>
        ParseMethodsDirectory(directory, pattern, DefaultMaxChars);

    public static IReadOnlyList<MethodCoverage> ParseMethodsDirectory(string directory, string pattern, long maxChars)
    {
        var files = FindReports(directory, pattern);
        if (files.Length is 0) return [];

        var methods = new MethodCollector();
        foreach (var file in files)
        {
            try
            {
                using var stream = File.OpenRead(file);
                using var reader = CreateReader(stream, maxChars, async: false);
                CollectMethods(reader, methods);
            }
            catch (XmlException ex)
            {
                throw new XmlException(
                    $"{file}: {LocationSentencePattern().Replace(ex.Message, "")}",
                    ex, ex.LineNumber, ex.LinePosition);
            }
        }

        return methods.Materialize();
    }

    public static IReadOnlyList<MethodCoverage> ParseMethodsPath(string path) => ParseMethodsPath(path, DefaultMaxChars);

    public static IReadOnlyList<MethodCoverage> ParseMethodsPath(string path, long maxChars)
    {
        if (File.Exists(path))
            return ParseMethodsFile(path, maxChars);
        if (Directory.Exists(path))
            return ParseMethodsDirectory(path, DefaultPattern, maxChars);

        throw new FileNotFoundException($"No file or directory at '{path}'.");
    }

    /// <summary>
    /// Walk one document into <paramref name="methods"/>. Source roots are per-document; the
    /// decoding warnings this walk raises have no channel on the list-returning API and are
    /// re-observable through the file-level parse of the same document.
    /// </summary>
    private static void CollectMethods(XmlReader reader, MethodCollector methods)
    {
        var document = new DocumentContext();
        while (reader.Read())
            Visit(reader, document, files: null, methods);
    }

    // ── Method aggregation ────────────────────────────────────────────────────

    /// <summary>
    /// Owns method identity, cross-document merging, and result materialization. It only ever
    /// sees decoded data: a <see cref="MethodKey"/>, an already-validated complexity, and
    /// <see cref="LineData"/> — never the XML.
    /// </summary>
    private sealed class MethodCollector
    {
        private readonly Dictionary<MethodKey, Entry> _methods = new();
        private readonly List<Entry> _order = [];

        public Entry For(MethodKey key, int? complexity)
        {
            if (!_methods.TryGetValue(key, out var entry))
            {
                _methods[key] = entry = new Entry(key);
                _order.Add(entry);
            }

            entry.MergeComplexity(complexity);
            return entry;
        }

        public IReadOnlyList<MethodCoverage> Materialize()
        {
            var result = new List<MethodCoverage>(_order.Count);
            foreach (var entry in _order)
                result.Add(entry.Materialize());
            return result;
        }

        public sealed class Entry(MethodKey key)
        {
            private readonly Dictionary<int, int> _lineHits = new();
            private int? _complexity;

            /// <summary>Same method entry across class blocks or documents: per line with <c>Math.Max</c>.</summary>
            public void AddLine(LineData line) =>
                _lineHits[line.Number] = _lineHits.TryGetValue(line.Number, out var prev)
                    ? Math.Max(prev, line.Hits)
                    : line.Hits;

            /// <summary>Complexity merges by <c>Math.Max</c>, like every other per-method datum.</summary>
            public void MergeComplexity(int? complexity)
            {
                if (complexity is { } c)
                    _complexity = _complexity is { } existing ? Math.Max(existing, c) : c;
            }

            public MethodCoverage Materialize()
            {
                var linesHit = 0;
                var start = 0;
                var end = 0;
                foreach (var (line, hits) in _lineHits)
                {
                    if (hits > 0) linesHit++;
                    if (start is 0 || line < start) start = line;
                    if (line > end) end = line;
                }

                return new MethodCoverage(
                    key.ClassName, key.MethodName, key.Signature, key.File,
                    start, end, linesHit, _lineHits.Count, _complexity)
                {
                    LineHits = new ReadOnlyDictionary<int, int>(_lineHits)
                };
            }
        }
    }
}
