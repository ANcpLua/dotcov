using System.Collections.ObjectModel;
using System.Xml;

namespace DotCov;

/// <summary>Identity of one Cobertura <c>&lt;method&gt;</c> entry within a document set.</summary>
internal readonly record struct MethodKey(string File, string ClassName, string MethodName, string Signature);

public static partial class CoberturaParser
{
    /// <summary>
    /// Parse per-method coverage detail from a Cobertura document the caller owns (the stream
    /// is not disposed). Entries are RAW per-method records — one per distinct (file, class,
    /// method name, signature) — including compiler-synthesized classes (state machines,
    /// lambda display classes); interpretation belongs to <see cref="CrapAnalysis"/>. Reports
    /// without <c>&lt;methods&gt;</c> detail (the original Cobertura summary shape, MTP's
    /// emitter) produce an empty method list, never a throw.
    /// </summary>
    public static MethodCoverageReport ParseMethods(Stream stream, long maxChars = DefaultMaxChars)
    {
        using var reader = CreateReader(stream, maxChars, async: false);
        var methods = new MethodCollector();
        CollectMethods(reader, methods);
        return methods.Materialize();
    }

    /// <summary>
    /// Parse one input; the stream is opened and disposed here. Malformed XML surfaces as
    /// <see cref="ReportParseException"/> naming the input.
    /// </summary>
    public static MethodCoverageReport ParseMethods(ReportInput input, long maxChars = DefaultMaxChars)
    {
        ArgumentNullException.ThrowIfNull(input);
        return ParseMethods([input], maxChars);
    }

    /// <summary>
    /// Parse every input into one method set. The same method entry across inputs merges per
    /// line with <c>Math.Max</c>, mirroring the file-level union-with-max semantics; warnings
    /// and source roots of every document are carried into the result.
    /// </summary>
    public static MethodCoverageReport ParseMethods(IEnumerable<ReportInput> inputs, long maxChars = DefaultMaxChars)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var methods = new MethodCollector();
        foreach (var input in inputs)
        {
            using var stream = input.OpenStream();
            try
            {
                using var reader = CreateReader(stream, maxChars, async: false);
                CollectMethods(reader, methods);
            }
            catch (XmlException ex)
            {
                throw new ReportParseException(input.SourceName, ex);
            }
        }

        return methods.Materialize();
    }

    public static MethodCoverageReport ParseMethodsFile(string path, long maxChars = DefaultMaxChars) =>
        ParseMethods(ReportInput.FromFile(path), maxChars);

    public static MethodCoverageReport ParseMethodsDirectory(string directory, string pattern = DefaultPattern) =>
        ParseMethodsDirectory(directory, pattern, DefaultMaxChars);

    public static MethodCoverageReport ParseMethodsDirectory(string directory, string pattern, long maxChars) =>
        ParseMethods(ReportResolver.ResolveDirectory(directory, ReportPattern.Parse(pattern)), maxChars);

    public static MethodCoverageReport ParseMethodsPath(string path) => ParseMethodsPath(path, DefaultMaxChars);

    public static MethodCoverageReport ParseMethodsPath(string path, long maxChars) =>
        ParseMethods(ReportResolver.Resolve(path), maxChars);

    /// <summary>Walk one document into <paramref name="methods"/>; roots are per document, warnings accumulate.</summary>
    private static void CollectMethods(XmlReader reader, MethodCollector methods)
    {
        var document = new DocumentContext();
        while (reader.Read())
            Visit(reader, document, files: null, methods);
        methods.Complete(document);
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
        private readonly List<CoverageWarning> _warnings = [];
        private readonly List<string> _sourceRoots = [];

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

        /// <summary>Fold one finished document's diagnostics and roots into the result (same root rules as the file report).</summary>
        public void Complete(DocumentContext document)
        {
            _warnings.AddRange(document.Warnings);
            if (document.SourceRoots is [""]) return;
            foreach (var root in document.SourceRoots)
                if (!_sourceRoots.Any(r => string.Equals(PathIdentity.NormalizeRoot(r), PathIdentity.NormalizeRoot(root), StringComparison.Ordinal)))
                    _sourceRoots.Add(root);
        }

        public MethodCoverageReport Materialize()
        {
            var result = new List<MethodCoverage>(_order.Count);
            foreach (var entry in _order)
                result.Add(entry.Materialize());
            return new MethodCoverageReport(result, _warnings, _sourceRoots);
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
