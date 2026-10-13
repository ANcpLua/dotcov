using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Xml;
using static System.FormattableString;

namespace DotCov;

/// <summary>
/// Streaming Cobertura XML parser: an <see cref="XmlReader"/> cursor walks the document, so
/// the XML is never held in memory. Secure by construction: <c>DtdProcessing.Ignore</c> plus
/// <c>XmlResolver = null</c> (DOCTYPE skipped, entities never resolve, so XXE payloads still
/// throw) and a per-document character cap.
/// <para>
/// Both aggregations share one traversal and one set of decoding rules (file-name identity,
/// line numbers, hit counts); they differ only in what they collect. The file aggregation
/// (<see cref="Parse(Stream, long, FileIdentity)"/>) folds every <c>&lt;line&gt;</c> of a source file into
/// one per-file line set — including the class-level summary lines. The method aggregation
/// (<see cref="ParseMethods(Stream, long, FileIdentity)"/>) keeps every <c>&lt;method&gt;</c> distinct and
/// ignores the class-level summary.
/// </para>
/// </summary>
public static partial class CoberturaParser
{
    private const long DefaultMaxChars = 50_000_000;

    /// <summary>
    /// Parse one document the caller owns (the stream is not disposed). <paramref name="identity"/>
    /// decides how relative filenames resolve under several declared roots; null is
    /// <see cref="FileIdentity.Lexical"/>.
    /// </summary>
    public static CoverageReport Parse(Stream stream, long maxChars = DefaultMaxChars, FileIdentity? identity = null)
    {
        var scope = new IdentityScope(identity);
        var report = ParseDocument(stream, maxChars, scope, sourceName: null);
        scope.RequireDeclaredRoot();
        return report;
    }

    /// <inheritdoc cref="Parse(Stream, long, FileIdentity)"/>
    public static async Task<CoverageReport> ParseAsync(
        Stream stream, long maxChars = DefaultMaxChars, FileIdentity? identity = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var scope = new IdentityScope(identity);
        using var reader = CreateReader(stream, maxChars, async: true);
        var document = new DocumentContext(scope, sourceName: null);
        var files = new FileCollector(document);

        while (await reader.ReadAsync())
        {
            ct.ThrowIfCancellationRequested();
            await VisitAsync(reader, document, files, ct);
        }

        ct.ThrowIfCancellationRequested();
        document.Finish();
        scope.RequireDeclaredRoot();
        return files.Materialize();
    }

    /// <summary>
    /// Parse one input. The stream is opened through <see cref="ReportInput.OpenStream"/> and
    /// disposed here, whatever happens. Malformed XML surfaces as
    /// <see cref="ReportParseException"/> naming the input.
    /// </summary>
    public static CoverageReport Parse(ReportInput input, long maxChars = DefaultMaxChars, FileIdentity? identity = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var scope = new IdentityScope(identity);
        var report = ParseInput(input, maxChars, scope);
        scope.RequireDeclaredRoot();
        return report;
    }

    /// <summary>
    /// Parse and merge every input in the given order. An empty input set yields an empty
    /// report; whether that is acceptable is the caller's decision (see <see cref="ReportResolver"/>).
    /// </summary>
    public static CoverageReport Parse(IEnumerable<ReportInput> inputs, long maxChars = DefaultMaxChars, FileIdentity? identity = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var scope = new IdentityScope(identity);
        CoverageReport? merged = null;
        foreach (var input in inputs)
        {
            ArgumentNullException.ThrowIfNull(input);
            var report = ParseInput(input, maxChars, scope);
            merged = merged is null ? report : CoverageReport.Merge(merged, report);
        }

        scope.RequireDeclaredRoot();
        return merged ?? CoverageReport.Empty;
    }

    private static CoverageReport ParseInput(ReportInput input, long maxChars, IdentityScope scope)
    {
        using var stream = input.OpenStream();
        try
        {
            return ParseDocument(stream, maxChars, scope, input.SourceName);
        }
        catch (XmlException ex)
        {
            throw new ReportParseException(input.SourceName, ex);
        }
    }

    private static CoverageReport ParseDocument(Stream stream, long maxChars, IdentityScope scope, string? sourceName)
    {
        using var reader = CreateReader(stream, maxChars, async: false);
        var document = new DocumentContext(scope, sourceName);
        var files = new FileCollector(document);

        while (reader.Read())
            Visit(reader, document, files, methods: null);

        document.Finish();
        return files.Materialize();
    }

    // ── XML reader ────────────────────────────────────────────────────────────

    /// <summary>The only <see cref="XmlReader.Create(Stream, XmlReaderSettings)"/> call site for Cobertura input.</summary>
    private static XmlReader CreateReader(Stream stream, long maxChars, bool async) =>
        XmlReader.Create(stream, new XmlReaderSettings
        {
            // Ignore, not Prohibit: reference Cobertura, gcovr, and coverage.py all emit a
            // DOCTYPE on every report, so Prohibit would reject the format's canonical emitters.
            // With XmlResolver = null external entities can never resolve and an entity
            // reference in content still throws.
            DtdProcessing = DtdProcessing.Ignore,
            MaxCharactersFromEntities = 1024,
            XmlResolver = null,
            IgnoreWhitespace = true,
            MaxCharactersInDocument = maxChars,
            Async = async
        });

    // ── Traversal ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The file-identity policy of one parse call and what its documents declared. One per
    /// call, never shared: the explicit-root check spans every document of the call (some
    /// document must declare the chosen root), so it needs state no single document has.
    /// </summary>
    private sealed class IdentityScope(FileIdentity? identity)
    {
        private readonly List<string> _declared = [];
        private bool _anyDocument;

        public FileIdentity Identity { get; } = identity ?? FileIdentity.Lexical;

        public void Record(IReadOnlyList<string> roots)
        {
            _anyDocument = true;
            foreach (var root in roots)
                if (!_declared.Contains(root)) _declared.Add(root);
        }

        /// <summary>An explicitly chosen root that no document of the call declares is an invalid request.</summary>
        public void RequireDeclaredRoot()
        {
            if (Identity.SourceRoot is { } root && _anyDocument && !_declared.Contains(root))
                throw new UndeclaredSourceRootException(root, _declared, sourceName: null);
        }
    }

    /// <summary>
    /// Per-document state: the declared source roots, the warnings raised while decoding, and the
    /// files whose identity the roots left unresolved.
    /// </summary>
    private sealed class DocumentContext(IdentityScope scope, string? sourceName)
    {
        public readonly List<string> SourceRoots = [];
        public readonly List<CoverageWarning> Warnings = [];
        public readonly List<AmbiguousFile> AmbiguousFiles = [];
        private readonly HashSet<CoverageWarning> _seen = [];
        private readonly HashSet<string> _ambiguous = new(StringComparer.Ordinal);

        public FileIdentity Identity => scope.Identity;

        public string? SourceName => sourceName;

        /// <summary>
        /// Record a warning once per document: coverlet repeats every line under its method and
        /// again in the class summary, and one malformed source line is one anomaly.
        /// </summary>
        public void Warn(CoverageWarning warning)
        {
            if (_seen.Add(warning)) Warnings.Add(warning);
        }

        /// <summary>Record a file keyed under the first of several candidate roots, once per document.</summary>
        public void MarkAmbiguous(string key)
        {
            if (_ambiguous.Add(key)) AmbiguousFiles.Add(new AmbiguousFile(key, SourceRoots.ToArray()));
        }

        /// <summary>The document is read to its end: its roots count toward the call's explicit-root check.</summary>
        public void Finish() => scope.Record(SourceRoots);
    }

    /// <summary>
    /// One step of the document walk. In Cobertura document order <c>&lt;sources&gt;</c>
    /// precedes <c>&lt;packages&gt;</c>, so the roots are complete before the first
    /// <c>&lt;class&gt;</c> arrives — no second pass needed.
    /// </summary>
    private static void Visit(XmlReader reader, DocumentContext document, FileCollector? files, MethodCollector? methods)
    {
        if (reader.NodeType is not XmlNodeType.Element) return;

        switch (reader.LocalName)
        {
            case "source":
                ConsumeSource(reader, document);
                break;
            case "class":
                ConsumeClass(reader, document, files, methods);
                break;
        }
    }

    private static async ValueTask VisitAsync(
        XmlReader reader, DocumentContext document, FileCollector files, CancellationToken ct)
    {
        if (reader.NodeType is not XmlNodeType.Element) return;

        switch (reader.LocalName)
        {
            case "source":
                if (reader.IsEmptyElement) return;
                var sourceDepth = reader.Depth;
                string? root = null;
                while (await reader.ReadAsync())
                {
                    ct.ThrowIfCancellationRequested();
                    if (reader.Depth == sourceDepth) break;   // </source>
                    if (IsOwnText(reader, sourceDepth)) root += await reader.GetValueAsync();
                }
                if (root is not null) RecordSource(root, document);
                break;
            case "class":
                await ConsumeClassAsync(reader, document, files, ct);
                break;
        }
    }

    /// <summary>
    /// Capture one <c>&lt;source&gt;</c> root. No-op spellings (".", "./") collapse to the ""
    /// sentinel, which <see cref="ResolveFileKey"/> reads as "leave relative filenames
    /// unprefixed". The sentinel is recorded, not discarded: a no-op declared alongside real
    /// roots is a second resolution convention, so it is a candidate root like any other and
    /// keeps its document-order slot (the FIRST declared root is the deterministic key).
    /// Respellings of one root are deduplicated so they cannot fake that multiplicity.
    /// <para>
    /// The root is the element's own character content: every text and CDATA child,
    /// concatenated. A comment or processing instruction splits that text without ending it,
    /// and nested elements are not part of it; an element with no text declares no root.
    /// <c>ReadSubtree</c> leaves the reader on the closing tag, so the next root is not skipped.
    /// </para>
    /// </summary>
    private static void ConsumeSource(XmlReader reader, DocumentContext document)
    {
        string? root = null;
        using (var sub = reader.ReadSubtree())
        {
            while (sub.Read())
                if (IsOwnText(sub, elementDepth: 0)) root += sub.Value;
        }

        if (root is not null) RecordSource(root, document);
    }

    /// <summary>A text or CDATA node that is a direct child of the element at <paramref name="elementDepth"/>.</summary>
    private static bool IsOwnText(XmlReader reader, int elementDepth) =>
        reader.Depth == elementDepth + 1 && reader.NodeType is (XmlNodeType.Text or XmlNodeType.CDATA);

    private static void RecordSource(string value, DocumentContext document)
    {
        var root = PathIdentity.NormalizeRoot(value);
        if (!document.SourceRoots.Contains(root)) document.SourceRoots.Add(root);   // List.Contains is Ordinal for strings
    }

    /// <summary>
    /// Walk one <c>&lt;class&gt;</c> subtree once and feed both collectors. Lines under
    /// <c>&lt;methods&gt;&lt;method&gt;</c> reach the method collector (attributed to the enclosing
    /// method) and reach the file collector only when the class has no class-level
    /// <c>&lt;lines&gt;</c>; that summary, when present, alone decides the file's lines.
    /// <c>ReadSubtree</c> leaves the outer reader on the closing tag when done. The async walk
    /// (<see cref="ConsumeClassAsync"/>) ends in the same <see cref="ClassContext.Complete"/>.
    /// </summary>
    private static void ConsumeClass(XmlReader reader, DocumentContext document, FileCollector? files, MethodCollector? methods)
    {
        if (CreateClassContext(reader, document, files, methods) is not { } context) return;
        using (var sub = reader.ReadSubtree())
        {
            while (sub.Read())
                context.Visit(sub);
        }

        context.Complete();
    }

    private static async ValueTask ConsumeClassAsync(
        XmlReader reader, DocumentContext document, FileCollector files, CancellationToken ct)
    {
        if (CreateClassContext(reader, document, files, methods: null) is not { } context) return;
        if (!reader.IsEmptyElement)
        {
            var depth = reader.Depth;
            // Not ReadSubtree: disposing an unfinished subtree reader drains it synchronously,
            // including on cancellation.
            while (await reader.ReadAsync())
            {
                ct.ThrowIfCancellationRequested();
                if (reader.NodeType is XmlNodeType.EndElement && reader.Depth == depth) break;
                context.Visit(reader);
            }
        }

        context.Complete();
    }

    private static ClassContext? CreateClassContext(
        XmlReader reader, DocumentContext document, FileCollector? files, MethodCollector? methods) =>
        DecodeFileName(reader, document) is { } file
            ? new ClassContext(file, reader.GetAttribute("name") ?? "", document, files?.For(file), methods)
            : null;

    private sealed class ClassContext(
        string file, string className, DocumentContext document, LineAccumulator? fileAcc, MethodCollector? methods)
    {
        private MethodCollector.Entry? _methodAcc;
        // Method lines wait for the class end: istanbul lists each function's declaration line
        // there, which is not a line of its own line coverage, so a class-level summary wins.
        private readonly LineAccumulator? _methodLines = fileAcc is null ? null : new LineAccumulator(file, document);
        private bool _inMethod;
        // The class-level <lines> ELEMENT, not a line under it: an empty summary (<lines/>) is
        // still the class's own statement that it has no lines.
        private bool _hasClassLines;

        private LineAccumulator? LineTarget => _inMethod ? _methodLines : fileAcc;

        /// <summary>
        /// The one finalisation of a class, shared by the sync and async walks: pending condition
        /// detail is validated, and method lines stand in for a class without a class-level summary.
        /// </summary>
        public void Complete()
        {
            _methodLines?.Flush();
            fileAcc?.Flush();
            if (!_hasClassLines) fileAcc?.AddFrom(_methodLines!);
        }

        public void Visit(XmlReader sub)
        {
            if (sub.NodeType is XmlNodeType.EndElement)
            {
                if (sub.LocalName is "method") EndMethod();
                return;
            }

            if (sub.NodeType is not XmlNodeType.Element) return;

            switch (sub.LocalName)
            {
                case "method":
                    BeginMethod(sub);
                    break;
                case "lines":
                    _hasClassLines |= !_inMethod;
                    break;
                case "line":
                    AddLine(sub);
                    break;
                case "condition":
                    LineTarget?.AddCondition(sub);
                    break;
            }
        }

        private void BeginMethod(XmlReader sub)
        {
            _methodAcc = methods?.For(
                new MethodKey(file, className, sub.GetAttribute("name") ?? "", sub.GetAttribute("signature") ?? ""),
                DecodeComplexity(sub.GetAttribute("complexity")));
            _inMethod = !sub.IsEmptyElement;
            if (sub.IsEmptyElement) _methodAcc = null;
        }

        private void EndMethod()
        {
            _methodAcc = null;
            _inMethod = false;
        }

        private void AddLine(XmlReader sub)
        {
            if (!TryDecodeLine(sub, file, document, out var line))
            {
                // A skipped line takes its nested <condition>s with it; they must not
                // attach to the previous branched line.
                LineTarget?.SkipLine();
                return;
            }
            LineTarget?.AddLine(sub, line);
            _methodAcc?.AddLine(line);
        }
    }

    // ── Shared decoding ───────────────────────────────────────────────────────

    /// <summary>A decoded <c>&lt;line&gt;</c>: the number and the (saturated) hit count.</summary>
    private readonly record struct LineData(int Number, int Hits);

    /// <summary>
    /// The file identity key of a <c>&lt;class filename&gt;</c>: separators normalized to '/'
    /// (Windows coverlet writes '\'), redundant <c>.</c> segments removed, the document's
    /// <c>&lt;source&gt;</c> root applied to relative names, and a leading drive letter
    /// uppercased. Both aggregations key on this string, so method entries join against
    /// <see cref="FileCoverage.Path"/> on the same key. Returns null for a class without a
    /// filename, which carries no attributable data.
    /// </summary>
    private static string? DecodeFileName(XmlReader reader, DocumentContext document) =>
        reader.GetAttribute("filename") is { } filename
            ? ResolveFileKey(PathIdentity.RemoveDotSegments(filename.Replace('\\', '/')), document)
            : null;

    /// <summary>
    /// Decode the line number and hit count shared by both aggregations. An unparseable number
    /// skips the line. Hits parse as long and saturate to int: counts above int.MaxValue are
    /// real (soak runs, 64-bit-counter emitters), and only &gt;0 matters downstream. A
    /// present-but-unparseable hits attribute warns and counts as 0; an absent one is a
    /// warning-free 0 (some emitters omit it).
    /// </summary>
    private static bool TryDecodeLine(XmlReader reader, string file, DocumentContext document, out LineData line)
    {
        line = default;
        if (!int.TryParse(reader.GetAttribute("number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            return false;

        var hits = 0;
        if (reader.GetAttribute("hits") is { } hitsAttr)
        {
            if (long.TryParse(hitsAttr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var h))
                hits = (int)Math.Clamp(h, int.MinValue, int.MaxValue);
            else if (BigInteger.TryParse(hitsAttr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var huge))
                // An integer beyond even long (an unsigned 64-bit counter) saturates the same way.
                hits = huge.Sign < 0 ? int.MinValue : int.MaxValue;
            else
                document.Warn(new CoverageWarning(
                    CoverageWarningKind.MalformedHits,
                    file,
                    number,
                    $"hits='{hitsAttr}' could not be parsed - treating as 0"));
        }

        line = new LineData(number, hits);
        return true;
    }

    /// <summary>
    /// A method-level <c>complexity</c> attribute, admitted only when it is a real measurement:
    /// coverlet emits integer cyclomatic complexity, but gcovr/grcov/cover2cover emit a
    /// placeholder <c>0</c>/<c>0.0</c> and ReportGenerator merges can produce <c>NaN</c>.
    /// Cyclomatic complexity is ≥ 1 by construction, so anything below 1 — including NaN,
    /// which fails every comparison — is "not measured", never a measurement of zero.
    /// </summary>
    private static int? DecodeComplexity(string? attr)
    {
        if (attr is null) return null;
        if (!double.TryParse(attr, NumberStyles.Float, CultureInfo.InvariantCulture, out var c)) return null;
        if (!(c >= 1) || c > int.MaxValue) return null;
        return (int)Math.Round(c, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Prepend the report's <c>&lt;source&gt;</c> root to a relative filename. Cobertura
    /// emitters (coverage.py, gcovr, cover2cover) write filenames relative to a source root;
    /// discarding it would make two different files that share a relative name collide on one key.
    /// The rooted check is manual (leading '/' or a drive-letter prefix) because
    /// <c>Path.IsPathRooted("C:/x")</c> is false on Linux and reports cross machines. A single
    /// root is applied unconditionally, never only on collision — a conditional prefix would make
    /// a file's identity unstable across runs. Several roots follow the parse's
    /// <see cref="FileIdentity"/> (<see cref="ResolveRoot"/>). A no-op root (the "" sentinel)
    /// resolves as "no prefix".
    /// </summary>
    private static string ResolveFileKey(string filename, DocumentContext document)
    {
        var roots = document.SourceRoots;
        if (roots.Count > 0 && !IsRooted(filename))
            filename = JoinRoot(roots.Count is 1 ? roots[0] : ResolveRoot(filename, document), filename);

        return PathIdentity.UppercaseDriveLetter(filename);
    }

    /// <summary>
    /// Pick the root for a relative filename in a document that declares several. Lexical (the
    /// default) never reads the disk: the first root is the deterministic key, and the file is
    /// recorded as ambiguous with one warning naming its candidate roots. Probing and an
    /// explicit root resolve it.
    /// </summary>
    private static string ResolveRoot(string filename, DocumentContext document) => document.Identity.Mode switch
    {
        FileIdentityMode.ProbeSources => ProbeRoot(filename, document),
        FileIdentityMode.SourceRoot => DeclaredRoot(document),
        _ => FirstRootAmbiguous(filename, document)
    };

    private static string FirstRootAmbiguous(string filename, DocumentContext document)
    {
        var roots = document.SourceRoots;
        var key = PathIdentity.UppercaseDriveLetter(JoinRoot(roots[0], filename));
        document.MarkAmbiguous(key);
        document.Warn(new CoverageWarning(
            CoverageWarningKind.FileIdentityAmbiguous,
            key,
            0,
            $"relative filename '{filename}' could belong to any of {roots.Count} <source> roots " +
            $"({string.Join(", ", roots.Select(static r => $"'{AmbiguousFile.Display(r)}'"))}) - " +
            $"keyed under the first, identity unresolved"));
        return roots[0];
    }

    /// <summary>
    /// <see cref="FileIdentityMode.ProbeSources"/>: coverage.py writes each file relative to the
    /// source root that contains it, so the one root under which the file exists on THIS disk is
    /// taken as the file's own. When it exists under none (a report read away from its sources)
    /// or under several, the first declared root applies and the document carries one
    /// report-scoped warning.
    /// </summary>
    private static string ProbeRoot(string filename, DocumentContext document)
    {
        string? match = null;
        foreach (var root in document.SourceRoots)
        {
            if (!File.Exists(JoinRoot(root, filename))) continue;
            if (match is not null) return FirstRootWithWarning(document);
            match = root;
        }

        return match ?? FirstRootWithWarning(document);
    }

    private static string FirstRootWithWarning(DocumentContext document)
    {
        var first = document.SourceRoots[0];
        document.Warn(new CoverageWarning(
            CoverageWarningKind.FileIdentityAmbiguous,
            "",
            0,
            $"multiple <source> roots - {(first.Length is 0
                ? "leaving relative filenames unprefixed (the first declared root is a no-op)"
                : $"resolving relative filenames against the first ('{first}')")}; files may not be attributable to a unique root"));
        return first;
    }

    /// <summary><see cref="FileIdentityMode.SourceRoot"/>: the chosen root, which a document with several roots must declare.</summary>
    private static string DeclaredRoot(DocumentContext document)
    {
        var root = document.Identity.SourceRoot!;
        return document.SourceRoots.Contains(root)
            ? root
            : throw new UndeclaredSourceRootException(root, document.SourceRoots.ToArray(), document.SourceName ?? "the report");
    }

    private static string JoinRoot(string root, string filename) =>
        root.Length is 0 ? filename : root.EndsWith('/') ? root + filename : $"{root}/{filename}";

    private static bool IsRooted(string path) =>
        path.StartsWith('/') ||
        (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':');

    // ── File aggregation ──────────────────────────────────────────────────────
    //
    // Cobertura emits one `<class>` block per IL type. A single source file routinely
    // produces several: the source class itself, each compiler-synthesized state-machine
    // class for async methods, each nested record's Equals/GetHashCode shim, and so on.
    // Within one block, every `<method><lines>` and the class-level summary `<lines>`
    // repeat the same line numbers with the same or different hit counts. Hit counts and
    // branch (Covered, Total) pairs are reconciled with Math.Max; per-condition detail
    // unions only between equal condition-number sets, exactly as FileCoverage.MergeWith
    // does across documents.

    private sealed class FileCollector(DocumentContext document)
    {
        // Ordinal, not OrdinalIgnoreCase: case-differing filenames are genuinely distinct
        // files on the Linux filesystems the format's native emitters run on. Windows
        // cross-report stability comes from normalizing the key itself in ResolveFileKey.
        private readonly Dictionary<string, LineAccumulator> _files = new(StringComparer.Ordinal);

        public LineAccumulator For(string file)
        {
            if (!_files.TryGetValue(file, out var acc))
                _files[file] = acc = new LineAccumulator(file, document);
            return acc;
        }

        public CoverageReport Materialize()
        {
            var result = new List<FileCoverage>(_files.Count);
            foreach (var (filename, acc) in _files)
                result.Add(acc.Materialize(filename));

            return new CoverageReport(result)
            {
                Warnings = document.Warnings,
                // A report whose ONLY declared root is the no-op sentinel exposes no roots at
                // all — lone <source>.</source> adds no identity information. Mixed declarations
                // keep the sentinel so Merge can tell ('.', '/real') apart from ('/real').
                SourceRoots = document.SourceRoots.Count is 1 && document.SourceRoots[0].Length is 0
                    ? []
                    : document.SourceRoots,
                AmbiguousFiles = document.AmbiguousFiles
            };
        }
    }

    /// <summary>
    /// One branched <c>&lt;line&gt;</c> whose <c>&lt;condition&gt;</c> children are being read:
    /// its own aggregate and the detail seen so far, validated together once the line ends.
    /// </summary>
    private sealed class PendingConditions(int line, int covered, int total)
    {
        public readonly int Line = line;
        public readonly int Covered = covered;
        public readonly int Total = total;
        public readonly Dictionary<int, int> Conditions = new();
        /// <summary>A condition whose number or coverage could not be parsed.</summary>
        public bool Malformed;
    }

    private sealed class LineAccumulator(string file, DocumentContext document)
    {
        private readonly Dictionary<int, int> _lineHits = new();

        // Per-line branch dedup: Coverlet emits the same branched line under
        // <methods>/<method>/<lines> AND <class>/<lines>, and a single source line may be
        // re-emitted under separate <class> blocks (record + state machine + partials).
        private readonly Dictionary<int, (int Covered, int Total)> _branchesByLine = new();

        // line → (coverlet condition `number` → covered outcomes of that 2-way jump, 0–2).
        // Keyed per condition so the cross-report merge can union by condition identity. An
        // EMPTY set is the poison sentinel: the line's blocks disagreed on condition identity.
        private readonly Dictionary<int, Dictionary<int, int>> _conditionsByLine = new();

        // Coverlet nests <conditions><condition number= coverage=/></conditions> inside each
        // branched <line>. In document order conditions follow their line, so they are
        // attributed to the most recent branched line; null means "no line to attribute to".
        private PendingConditions? _pending;

        /// <summary>A <c>&lt;line&gt;</c> that could not be decoded: conditions that follow belong to no line.</summary>
        public void SkipLine() => Flush();

        public void AddLine(XmlReader reader, LineData line)
        {
            Flush();
            MergeHits(line.Number, line.Hits);

            // Emitters disagree on casing: original Cobertura/JaCoCo write `branch="true"`,
            // Coverlet writes `branch="True"`. A literal compare would silently drop Coverlet
            // branches and render branch coverage as a fake 100%.
            if (!string.Equals(reader.GetAttribute("branch"), "true", StringComparison.OrdinalIgnoreCase))
                return;

            if (reader.GetAttribute("condition-coverage") is not { } cond)
            {
                Warn(CoverageWarningKind.MalformedConditionCoverage, line.Number,
                    "branch=\"true\" without a condition-coverage attribute - no branch counted");
                return;
            }

            if (!TryParseConditionCoverage(cond, out var covered, out var total))
            {
                Warn(CoverageWarningKind.MalformedConditionCoverage, line.Number,
                    $"condition-coverage='{cond}' could not be parsed");
                return;
            }

            MergeBranches(line.Number, covered, total);
            _pending = new PendingConditions(line.Number, covered, total);
        }

        public void AddCondition(XmlReader reader)
        {
            if (_pending is not { } pending) return;

            if (int.TryParse(reader.GetAttribute("number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) &&
                TryParseConditionOutcomes(reader.GetAttribute("coverage"), out var covered))
                // A number repeated within one line unions like every other per-line datum.
                pending.Conditions[number] = Math.Max(pending.Conditions.GetValueOrDefault(number), covered);
            else
                pending.Malformed = true;
        }

        /// <summary>
        /// Validate the pending line's detail against that line's own aggregate, then union it.
        /// Detail that does not describe 2-outcome jumps (two outcomes per condition do not make
        /// the line's total: coverlet's switch tables, grcov's one-condition-per-outcome counts)
        /// never stood for the aggregate and is left out, as it always was. Detail in that shape
        /// must also reproduce the covered count, or one run would claim more coverage than it
        /// reports — a condition at 100% would lift a <c>(0/2)</c> line to <c>(2/2)</c>. Such
        /// detail, and detail that cannot be parsed, is dropped with a warning.
        /// </summary>
        public void Flush()
        {
            if (_pending is not { } pending) return;
            _pending = null;

            if (pending.Malformed)
            {
                Warn(CoverageWarningKind.MalformedConditionCoverage, pending.Line,
                    "a <condition> could not be parsed - using the line aggregate");
                return;
            }

            if (pending.Conditions.Count is 0 || pending.Conditions.Count * 2 != pending.Total) return;

            var sum = pending.Conditions.Values.Sum();
            if (sum != pending.Covered)
            {
                Warn(CoverageWarningKind.MalformedConditionCoverage, pending.Line,
                    Invariant($"{pending.Conditions.Count} conditions with {sum} covered outcomes disagree with ") +
                    Invariant($"condition-coverage ({pending.Covered}/{pending.Total}) - using the line aggregate"));
                return;
            }

            MergeConditionSet(pending.Line, pending.Conditions);
        }

        /// <summary>Fold another accumulator's lines, branches and conditions into this one.</summary>
        public void AddFrom(LineAccumulator other)
        {
            foreach (var (line, hits) in other._lineHits) MergeHits(line, hits);
            foreach (var (line, (covered, total)) in other._branchesByLine) MergeBranches(line, covered, total);
            foreach (var (line, conds) in other._conditionsByLine) MergeConditionSet(line, conds);
        }

        private void MergeHits(int line, int hits) =>
            _lineHits[line] = _lineHits.TryGetValue(line, out var existing) ? Math.Max(existing, hits) : hits;

        private void MergeBranches(int line, int covered, int total) =>
            _branchesByLine[line] = _branchesByLine.TryGetValue(line, out var existing)
                ? (Math.Max(existing.Covered, covered), Math.Max(existing.Total, total))
                : (covered, total);

        /// <summary>
        /// The condition-identity rule of <see cref="FileCoverage.MergeWith"/>, applied between the
        /// blocks of one document: the same number set unions per number with <c>Math.Max</c>; a
        /// different set warns once and poisons the line to its raw aggregate, absorbingly.
        /// </summary>
        private void MergeConditionSet(int line, Dictionary<int, int> incoming)
        {
            if (!_conditionsByLine.TryGetValue(line, out var existing))
            {
                _conditionsByLine[line] = new Dictionary<int, int>(incoming);
                return;
            }

            // Either side already poisoned (it warned then): absorbing, no second warning.
            if (existing.Count is 0 || incoming.Count is 0)
            {
                existing.Clear();
                return;
            }

            if (existing.Count == incoming.Count && incoming.Keys.All(existing.ContainsKey))
            {
                foreach (var (number, covered) in incoming)
                    existing[number] = Math.Max(existing[number], covered);
                return;
            }

            Warn(CoverageWarningKind.ConditionIdentityMismatch, line,
                $"condition numbers [{string.Join(",", existing.Keys.Order())}] vs " +
                $"[{string.Join(",", incoming.Keys.Order())}] - using the line aggregate");
            existing.Clear();
        }

        private void Warn(CoverageWarningKind kind, int line, string detail) =>
            document.Warn(new CoverageWarning(kind, file, line, detail));

        public FileCoverage Materialize(string filename)
        {
            // Keep per-condition detail where it reconstructs the line aggregate as 2-outcome
            // jumps, and keep the poison sentinel. Detail can still fall short of a line whose
            // larger total came from a block without detail; it then drops to the line
            // aggregate so merge never invents a total the emitter didn't report — the
            // invariant Merge's per-condition union relies on.
            var conditionsByLine = new Dictionary<int, IReadOnlyDictionary<int, int>>();
            foreach (var (line, conds) in _conditionsByLine)
                // Every line in _conditionsByLine has a _branchesByLine entry by construction —
                // detail is only recorded after the aggregate is.
                if (conds.Count is 0 || conds.Count * 2 == _branchesByLine[line].Total)
                    conditionsByLine[line] = new Dictionary<int, int>(conds);

            return FileCoverage.FromLineData(filename, _lineHits, _branchesByLine, conditionsByLine);
        }
    }

    /// <summary>
    /// Read <c>(covered/total)</c> from a <c>condition-coverage</c> value. A fraction claiming
    /// more covered outcomes than the line has is malformed like any other unreadable value:
    /// accepting it would put BranchesHit above BranchesTotal (a &gt;100% branch rate).
    /// </summary>
    private static bool TryParseConditionCoverage(string cond, out int covered, out int total)
    {
        covered = 0;
        total = 0;
        var match = ConditionPattern().Match(cond);
        if (!match.Success) return false;
        return int.TryParse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture, out covered) &&
               int.TryParse(match.Groups[2].ValueSpan, CultureInfo.InvariantCulture, out total) &&
               covered <= total;
    }

    // coverlet's per-<condition> `coverage` is the percentage of that branch's outcomes hit.
    // Branches are 2-outcome jumps (taken/not-taken): 0% -> 0, 50% -> 1, 100% -> 2 covered.
    // A non-2-way figure (e.g. 33.33% from a switch arm) still parses, but Materialize's
    // 2-outcome consistency gate then drops that line back to the line-level aggregate.
    private static bool TryParseConditionOutcomes(string? coverage, out int covered)
    {
        covered = 0;
        if (coverage is null) return false;
        var span = coverage.AsSpan().TrimEnd('%');
        if (!double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
            return false;
        // Range-gate before rounding (NaN and ±Infinity fail too): a percent outside [0,100]
        // would put a covered value outside 0–2 into the per-condition map.
        if (percent is not (>= 0 and <= 100)) return false;
        covered = (int)Math.Round(percent / 100.0 * 2.0, MidpointRounding.AwayFromZero);
        return true;
    }

    [GeneratedRegex(@"\((\d+)/(\d+)\)")]
    private static partial Regex ConditionPattern();
}
