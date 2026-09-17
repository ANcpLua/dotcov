using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;

namespace DotCov;

/// <summary>
/// Streaming Cobertura XML parser: an <see cref="XmlReader"/> cursor walks the document, so
/// the XML is never held in memory. Secure by construction: <c>DtdProcessing.Ignore</c> plus
/// <c>XmlResolver = null</c> (DOCTYPE skipped, entities never resolve, so XXE payloads still
/// throw) and a per-document character cap.
/// <para>
/// Both aggregations share one traversal and one set of decoding rules (file-name identity,
/// line numbers, hit counts); they differ only in what they collect. The file aggregation
/// (<see cref="Parse(Stream, long)"/>) folds every <c>&lt;line&gt;</c> of a source file into
/// one per-file line set — including the class-level summary lines. The method aggregation
/// (<see cref="ParseMethods(Stream, long)"/>) keeps every <c>&lt;method&gt;</c> distinct and
/// ignores the class-level summary.
/// </para>
/// </summary>
public static partial class CoberturaParser
{
    private const long DefaultMaxChars = 50_000_000;
    private const string DefaultPattern = ReportPattern.DefaultText;

    public static CoverageReport Parse(Stream stream, long maxChars = DefaultMaxChars)
    {
        using var reader = CreateReader(stream, maxChars, async: false);
        var document = new DocumentContext();
        var files = new FileCollector();

        while (reader.Read())
            Visit(reader, document, files, methods: null);

        return files.Materialize(document);
    }

    public static async Task<CoverageReport> ParseAsync(
        Stream stream, long maxChars = DefaultMaxChars, CancellationToken ct = default)
    {
        using var reader = CreateReader(stream, maxChars, async: true);
        var document = new DocumentContext();
        var files = new FileCollector();

        while (await reader.ReadAsync())
        {
            ct.ThrowIfCancellationRequested();
            Visit(reader, document, files, methods: null);
        }

        return files.Materialize(document);
    }

    /// <summary>
    /// Parse one input. The stream is opened through <see cref="ReportInput.OpenStream"/> and
    /// disposed here, whatever happens. Malformed XML surfaces as
    /// <see cref="ReportParseException"/> naming the input.
    /// </summary>
    public static CoverageReport Parse(ReportInput input, long maxChars = DefaultMaxChars)
    {
        ArgumentNullException.ThrowIfNull(input);
        using var stream = input.OpenStream();
        try
        {
            return Parse(stream, maxChars);
        }
        catch (XmlException ex)
        {
            throw new ReportParseException(input.SourceName, ex);
        }
    }

    /// <summary>
    /// Parse and merge every input in the given order. An empty input set yields an empty
    /// report; whether that is acceptable is the caller's decision (see <see cref="ReportResolver"/>).
    /// </summary>
    public static CoverageReport Parse(IEnumerable<ReportInput> inputs, long maxChars = DefaultMaxChars)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        CoverageReport? merged = null;
        foreach (var input in inputs)
        {
            var report = Parse(input, maxChars);
            merged = merged is null ? report : CoverageReport.Merge(merged, report);
        }

        return merged ?? CoverageReport.Empty;
    }

    public static CoverageReport ParseFile(string path, long maxChars = DefaultMaxChars) =>
        Parse(ReportInput.FromFile(path), maxChars);

    public static CoverageReport ParseDirectory(string directory, string pattern = DefaultPattern) =>
        ParseDirectory(directory, pattern, DefaultMaxChars);

    public static CoverageReport ParseDirectory(string directory, string pattern, long maxChars) =>
        Parse(ReportResolver.ResolveDirectory(directory, ReportPattern.Parse(pattern)), maxChars);

    public static CoverageReport ParsePath(string path) => ParsePath(path, DefaultMaxChars);

    public static CoverageReport ParsePath(string path, long maxChars) =>
        Parse(ReportResolver.Resolve(path), maxChars);

    // ── XML reader ────────────────────────────────────────────────────────────

    /// <summary>The only <see cref="XmlReader.Create(Stream, XmlReaderSettings)"/> call site for Cobertura input.</summary>
    private static XmlReader CreateReader(Stream stream, long maxChars, bool async) =>
        XmlReader.Create(stream, new XmlReaderSettings
        {
            // Ignore, not Prohibit: reference Cobertura, gcovr, and coverage.py all emit a
            // DOCTYPE on every report, so Prohibit rejected the format's canonical emitters.
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

    /// <summary>Per-document state: the declared source roots and the warnings raised while decoding.</summary>
    private sealed class DocumentContext
    {
        public readonly List<string> SourceRoots = [];
        public readonly List<CoverageWarning> Warnings = [];
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

    /// <summary>
    /// Capture one <c>&lt;source&gt;</c> root. No-op spellings (".", "./") collapse to the ""
    /// sentinel, which <see cref="ResolveFileKey"/> reads as "leave relative filenames
    /// unprefixed". The sentinel is recorded, not discarded: a no-op declared alongside real
    /// roots is a second resolution convention, so it counts toward the multi-root warning and
    /// keeps its document-order slot (the FIRST declared root wins resolution). Respellings of
    /// one root are deduplicated so they cannot fake that multiplicity.
    /// </summary>
    private static void ConsumeSource(XmlReader reader, DocumentContext document)
    {
        if (reader.IsEmptyElement) return;
        if (!reader.Read() || reader.NodeType is not (XmlNodeType.Text or XmlNodeType.CDATA)) return;

        var root = PathIdentity.NormalizeRoot(reader.Value);
        if (document.SourceRoots.Contains(root)) return;   // List.Contains is Ordinal for strings

        document.SourceRoots.Add(root);
        if (document.SourceRoots.Count is 2)
            document.Warnings.Add(new CoverageWarning(
                CoverageWarningKind.FileIdentityAmbiguous,
                "",
                0,
                $"multiple <source> roots - {(document.SourceRoots[0].Length is 0
                    ? "leaving relative filenames unprefixed (the first declared root is a no-op)"
                    : $"resolving relative filenames against the first ('{document.SourceRoots[0]}')")}; files may not be attributable to a unique root"));
    }

    /// <summary>
    /// Walk one <c>&lt;class&gt;</c> subtree once and feed both collectors. Lines under
    /// <c>&lt;methods&gt;&lt;method&gt;</c> reach the file collector AND the method collector
    /// (attributed to the enclosing method); the trailing class-level <c>&lt;lines&gt;</c>
    /// summary reaches only the file collector. <c>ReadSubtree</c> leaves the outer reader on
    /// the closing tag when done.
    /// </summary>
    private static void ConsumeClass(XmlReader reader, DocumentContext document, FileCollector? files, MethodCollector? methods)
    {
        if (DecodeFileName(reader, document) is not { } file) return;

        var className = reader.GetAttribute("name") ?? "";
        var fileAcc = files?.For(file);
        MethodCollector.Entry? methodAcc = null;

        using var sub = reader.ReadSubtree();
        sub.MoveToContent();

        while (sub.Read())
        {
            switch (sub.NodeType, sub.LocalName)
            {
                case (XmlNodeType.Element, "method"):
                    methodAcc = methods?.For(
                        new MethodKey(file, className, sub.GetAttribute("name") ?? "", sub.GetAttribute("signature") ?? ""),
                        DecodeComplexity(sub.GetAttribute("complexity")));
                    if (sub.IsEmptyElement) methodAcc = null;
                    break;

                case (XmlNodeType.EndElement, "method"):
                    methodAcc = null;
                    break;

                case (XmlNodeType.Element, "line"):
                    if (!TryDecodeLine(sub, file, document, out var line)) break;
                    fileAcc?.AddLine(sub, line, file, document);
                    methodAcc?.AddLine(line);
                    break;

                case (XmlNodeType.Element, "condition"):
                    fileAcc?.AddCondition(sub);
                    break;
            }
        }
    }

    // ── Shared decoding ───────────────────────────────────────────────────────

    /// <summary>A decoded <c>&lt;line&gt;</c>: the number and the (saturated) hit count.</summary>
    private readonly record struct LineData(int Number, int Hits);

    /// <summary>
    /// The file identity key of a <c>&lt;class filename&gt;</c>: separators normalized to '/'
    /// (Windows coverlet writes '\'), the document's first <c>&lt;source&gt;</c> root applied
    /// to relative names, and a leading drive letter uppercased. Both aggregations key on this
    /// string, so method entries join against <see cref="FileCoverage.Path"/> on the same key.
    /// Returns null for a class without a filename, which carries no attributable data.
    /// </summary>
    private static string? DecodeFileName(XmlReader reader, DocumentContext document) =>
        reader.GetAttribute("filename") is { } filename
            ? ResolveFileKey(filename.Replace('\\', '/'), document.SourceRoots)
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
            else
                document.Warnings.Add(new CoverageWarning(
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
    /// Prepend the report's first <c>&lt;source&gt;</c> root to a relative filename. Cobertura
    /// emitters (coverage.py, gcovr, cover2cover) write filenames relative to a source root;
    /// discarding it made two different files that share a relative name collide on one key.
    /// The rooted check is manual (leading '/' or a drive-letter prefix) because
    /// <c>Path.IsPathRooted("C:/x")</c> is false on Linux and reports cross machines. The root
    /// is applied unconditionally, never only on collision — a conditional prefix would make a
    /// file's identity unstable across runs. A first-declared no-op root (the "" sentinel)
    /// resolves as "no prefix": first-wins applies to the DECLARED order.
    /// </summary>
    private static string ResolveFileKey(string filename, List<string> sourceRoots)
    {
        if (sourceRoots.Count > 0 && sourceRoots[0].Length > 0 && !IsRooted(filename))
        {
            var root = sourceRoots[0];
            filename = root.EndsWith('/') ? root + filename : $"{root}/{filename}";
        }

        // Uppercase a leading drive letter so `c:\x\A.cs` and `C:/x/A.cs` — the same file
        // emitted by different Windows toolchains — produce one Ordinal key. Key normalization,
        // not a case-insensitive comparer: a Dictionary has a single comparer for every key.
        if (filename.Length >= 3 && char.IsAsciiLetterLower(filename[0]) && filename[1] == ':' && filename[2] == '/')
            filename = char.ToUpperInvariant(filename[0]) + filename[1..];

        return filename;
    }

    private static bool IsRooted(string path) =>
        path.StartsWith('/') ||
        (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':');

    // ── File aggregation ──────────────────────────────────────────────────────
    //
    // Cobertura emits one `<class>` block per IL type. A single source file routinely
    // produces several: the source class itself, each compiler-synthesized state-machine
    // class for async methods, each nested record's Equals/GetHashCode shim, and so on.
    // Within one block, every `<method><lines>` and the class-level summary `<lines>`
    // repeat the same line numbers with the same or different hit counts. Every per-line
    // datum — hit counts, branch (Covered, Total) pairs, per-condition outcomes — is
    // reconciled with Math.Max.

    private sealed class FileCollector
    {
        // Ordinal, not OrdinalIgnoreCase: case-differing filenames are genuinely distinct
        // files on the Linux filesystems the format's native emitters run on. Windows
        // cross-report stability comes from normalizing the key itself in ResolveFileKey.
        private readonly Dictionary<string, LineAccumulator> _files = new(StringComparer.Ordinal);

        public LineAccumulator For(string file)
        {
            if (!_files.TryGetValue(file, out var acc))
                _files[file] = acc = new LineAccumulator();
            return acc;
        }

        public CoverageReport Materialize(DocumentContext document)
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
                SourceRoots = document.SourceRoots is [""] ? [] : document.SourceRoots
            };
        }
    }

    private sealed class LineAccumulator
    {
        private readonly Dictionary<int, int> _lineHits = new();

        // Per-line branch dedup: Coverlet emits the same branched line under
        // <methods>/<method>/<lines> AND <class>/<lines>, and a single source line may be
        // re-emitted under separate <class> blocks (record + state machine + partials).
        private readonly Dictionary<int, (int Covered, int Total)> _branchesByLine = new();

        // line → (coverlet condition `number` → covered outcomes of that 2-way jump, 0–2).
        // Keyed per condition so the cross-report merge can union by condition identity.
        private readonly Dictionary<int, Dictionary<int, int>> _conditionsByLine = new();

        // Coverlet nests <conditions><condition number= coverage=/></conditions> inside each
        // branched <line>. In document order conditions follow their line, so they are
        // attributed to the most recent branched line; -1 means "no per-condition detail".
        private int _conditionLine = -1;

        public void AddLine(XmlReader reader, LineData line, string file, DocumentContext document)
        {
            _conditionLine = -1;
            _lineHits[line.Number] = _lineHits.TryGetValue(line.Number, out var existing)
                ? Math.Max(existing, line.Hits)
                : line.Hits;

            // Emitters disagree on casing: original Cobertura/JaCoCo write `branch="true"`,
            // Coverlet writes `branch="True"`. A literal compare silently dropped Coverlet
            // branches and rendered branch coverage as a fake 100%.
            if (!string.Equals(reader.GetAttribute("branch"), "true", StringComparison.OrdinalIgnoreCase) ||
                reader.GetAttribute("condition-coverage") is not { } cond)
                return;

            if (TryParseConditionCoverage(cond, out var covered, out var total))
            {
                _branchesByLine[line.Number] = _branchesByLine.TryGetValue(line.Number, out var existingBranch)
                    ? (Math.Max(existingBranch.Covered, covered), Math.Max(existingBranch.Total, total))
                    : (covered, total);
                _conditionLine = line.Number;
            }
            else
            {
                document.Warnings.Add(new CoverageWarning(
                    CoverageWarningKind.MalformedConditionCoverage,
                    file,
                    line.Number,
                    $"condition-coverage='{cond}' could not be parsed"));
            }
        }

        public void AddCondition(XmlReader reader)
        {
            if (_conditionLine < 0) return;
            if (!int.TryParse(reader.GetAttribute("number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) return;
            if (!TryParseConditionOutcomes(reader.GetAttribute("coverage"), out var covered)) return;

            if (!_conditionsByLine.TryGetValue(_conditionLine, out var conds))
                _conditionsByLine[_conditionLine] = conds = new Dictionary<int, int>();
            conds[number] = conds.TryGetValue(number, out var existing) ? Math.Max(existing, covered) : covered;
        }

        public FileCoverage Materialize(string filename)
        {
            // Keep per-condition detail only where it reconstructs the line aggregate as
            // 2-outcome jumps (the universal case for &&/||/?:/??/?.). If a switch jump-table
            // makes it inconsistent, drop to the line aggregate so merge never invents a total
            // the emitter didn't report — the invariant Merge's per-condition union relies on.
            var conditionsByLine = new Dictionary<int, IReadOnlyDictionary<int, int>>();
            foreach (var (line, conds) in _conditionsByLine)
                // Every line in _conditionsByLine has a _branchesByLine entry by construction —
                // AddCondition only fires after the aggregate is recorded.
                if (conds.Count * 2 == _branchesByLine[line].Total)
                    conditionsByLine[line] = new Dictionary<int, int>(conds);

            return FileCoverage.FromLineData(filename, _lineHits, _branchesByLine, conditionsByLine);
        }
    }

    private static bool TryParseConditionCoverage(string cond, out int covered, out int total)
    {
        covered = 0;
        total = 0;
        var match = ConditionPattern().Match(cond);
        if (!match.Success) return false;
        return int.TryParse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture, out covered) &&
               int.TryParse(match.Groups[2].ValueSpan, CultureInfo.InvariantCulture, out total);
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
