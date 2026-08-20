using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;

namespace DotCov;

/// <summary>
/// Streaming Cobertura XML parser. Same pattern as AccessReportXml:
/// XmlReader cursor walks the document — XML never held in memory.
/// Secure: DtdProcessing.Ignore + XmlResolver = null (DOCTYPE skipped, entities never
/// resolve, so XXE payloads still throw), character cap.
/// </summary>
public static partial class CoberturaParser
{
    private const long DefaultMaxChars = 50_000_000;
    private const string DefaultPattern = "**/coverage.cobertura.xml";

    public static CoverageReport Parse(Stream stream, long maxChars = DefaultMaxChars)
    {
        using var reader = XmlReader.Create(stream, CreateSecureSettings(maxChars));
        return ParseCore(reader);
    }

    public static async Task<CoverageReport> ParseAsync(
        Stream stream, long maxChars = DefaultMaxChars, CancellationToken ct = default)
    {
        using var reader = XmlReader.Create(stream, CreateSecureSettings(maxChars, async: true));
        return await ParseCoreAsync(reader, ct);
    }

    public static CoverageReport ParseFile(string path, long maxChars = DefaultMaxChars)
    {
        using var stream = File.OpenRead(path);
        return Parse(stream, maxChars);
    }

    /// <summary>
    /// Parse and merge every matching report under <paramref name="directory"/>. Only two
    /// pattern shapes are supported: <c>filename</c> (top level only) and <c>**/filename</c>
    /// (recursive). Any other directory component throws instead of silently matching
    /// nothing — a glob that quietly matches zero files flows into
    /// <see cref="CoverageReport.Evaluate"/> as "nothing was measured", the most invisible
    /// possible misconfiguration.
    /// </summary>
    public static CoverageReport ParseDirectory(string directory, string pattern = DefaultPattern)
    {
        var name = Path.GetFileName(pattern);
        if (pattern[..^name.Length] is not ("" or "**/") || name.Contains('\\'))
            throw new ArgumentException(
                $"Unsupported pattern '{pattern}': only 'filename' and '**/filename' are supported.",
                nameof(pattern));

        var files = Directory.GetFiles(directory, name,
            new EnumerationOptions { RecurseSubdirectories = pattern.Contains("**") });

        if (files.Length is 0)
            return CoverageReport.Empty;

        return files
            .OrderBy(static f => f, StringComparer.Ordinal)
            .Select(static f => ParseFile(f))
            .Aggregate(CoverageReport.Merge);
    }

    public static CoverageReport ParsePath(string path)
    {
        if (File.Exists(path))
            return ParseFile(path);
        if (Directory.Exists(path))
            return ParseDirectory(path);

        throw new FileNotFoundException($"No file or directory at '{path}'.");
    }

    private static XmlReaderSettings CreateSecureSettings(long maxChars, bool async = false) => new()
    {
        // Ignore, not Prohibit: reference Cobertura, gcovr, and coverage.py all emit
        // `<!DOCTYPE coverage SYSTEM "http://cobertura.sourceforge.net/xml/coverage-04.dtd">`
        // on every report, so Prohibit rejected the format's canonical emitters. Ignore skips
        // the DTD without processing it; with XmlResolver = null external entities can never
        // resolve and an entity reference in content still throws — XXE stays dead. The
        // entity-expansion cap is belt-and-braces for the same threat.
        DtdProcessing = DtdProcessing.Ignore,
        MaxCharactersFromEntities = 1024,
        XmlResolver = null,
        IgnoreWhitespace = true,
        MaxCharactersInDocument = maxChars,
        Async = async
    };

    // ── Aggregation primitive ─────────────────────────────────────────────────
    //
    // Cobertura emits one `<class>` block per IL type. A single source file routinely
    // produces several: the source class itself, each compiler-synthesized state-machine
    // class for async methods, each nested record's Equals/GetHashCode shim, and so on.
    // Within one block, every `<method><lines>` and the class-level summary `<lines>`
    // repeat the same line numbers with the same or different hit counts.
    //
    // We collect into Dictionary<filename, LineAccumulator> and reconcile each per-line
    // datum with Math.Max — both for hit counts and for branch (Covered, Total) pairs.

    private sealed class LineAccumulator
    {
        public readonly Dictionary<int, int> LineHits = new();

        // Per-line branch dedup: Coverlet emits the same branched line under
        // <methods>/<method>/<lines> AND <class>/<lines>, and a single source line may be
        // re-emitted under separate <class> blocks (record + state machine + partials).
        // Keying on line number with Math.Max prevents double-counting in all of those.
        public readonly Dictionary<int, (int Covered, int Total)> BranchesByLine = new();

        // line → (coverlet condition `number` → covered outcomes of that 2-way jump, 0–2).
        // Same Math.Max dedup as BranchesByLine, but keyed per condition so the cross-report
        // merge can union by condition identity instead of collapsing to a single count.
        public readonly Dictionary<int, Dictionary<int, int>> ConditionsByLine = new();

        public void AddCondition(int line, int number, int covered)
        {
            if (!ConditionsByLine.TryGetValue(line, out var conds))
                ConditionsByLine[line] = conds = new Dictionary<int, int>();
            conds[number] = conds.TryGetValue(number, out var existing) ? Math.Max(existing, covered) : covered;
        }
    }

    private static CoverageReport ParseCore(XmlReader reader)
    {
        var files = new Dictionary<string, LineAccumulator>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<CoverageWarning>();

        while (reader.Read())
        {
            if (reader is not { NodeType: XmlNodeType.Element, LocalName: "class" })
                continue;

            ConsumeClass(reader, files, warnings);
        }

        return Materialize(files, warnings);
    }

    private static async Task<CoverageReport> ParseCoreAsync(XmlReader reader, CancellationToken ct)
    {
        var files = new Dictionary<string, LineAccumulator>(StringComparer.OrdinalIgnoreCase);
        var warnings = new List<CoverageWarning>();

        while (await reader.ReadAsync())
        {
            ct.ThrowIfCancellationRequested();

            if (reader is not { NodeType: XmlNodeType.Element, LocalName: "class" })
                continue;

            ConsumeClass(reader, files, warnings);
        }

        return Materialize(files, warnings);
    }

    private static void ConsumeClass(
        XmlReader reader,
        Dictionary<string, LineAccumulator> files,
        List<CoverageWarning> warnings)
    {
        var filename = reader.GetAttribute("filename");
        if (filename is null) return;

        // Normalize path separators so the same source file merges across machines/CI jobs
        // regardless of emitter convention (Windows coverlet writes `\`, Linux writes `/`).
        // This string is the file's identity key in Materialize/Merge, so it must be stable.
        filename = filename.Replace('\\', '/');

        if (!files.TryGetValue(filename, out var acc))
        {
            acc = new LineAccumulator();
            files[filename] = acc;
        }

        // Walk the entire `<class>` subtree so that lines emitted under
        // `<methods><method><lines>` AND under the trailing `<lines>` summary
        // both contribute. ReadSubtree leaves the outer reader positioned on
        // the closing `</class>` tag when we're done.
        using var sub = reader.ReadSubtree();
        sub.MoveToContent();

        // Coverlet nests <conditions><condition number= coverage=/></conditions> inside each
        // branched <line>. In document order conditions follow their line, so we attribute them
        // to the most recent branched line; -1 means "current line carries no per-condition detail".
        var conditionLine = -1;

        while (sub.Read())
        {
            if (sub.NodeType != XmlNodeType.Element) continue;

            if (sub.LocalName == "condition")
            {
                if (conditionLine >= 0 &&
                    int.TryParse(sub.GetAttribute("number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var condNumber) &&
                    TryParseConditionOutcomes(sub.GetAttribute("coverage"), out var condCovered))
                {
                    acc.AddCondition(conditionLine, condNumber, condCovered);
                }
                continue;
            }

            if (sub.LocalName != "line") continue;
            conditionLine = -1;

            if (!int.TryParse(sub.GetAttribute("number"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lineNum))
                continue;

            var hitsAttr = sub.GetAttribute("hits");
            var hits = 0;
            if (hitsAttr is not null)
            {
                // Parse as long and saturate: hit counts above int.MaxValue are real (soak
                // runs, 64-bit-counter emitters like gcovr/llvm-cov), and only >0 matters
                // downstream — degrading overflow to 0 silently flipped covered lines to
                // misses. Present-but-unparseable warns instead of silently recording a miss;
                // an absent attribute stays a warning-free 0 (some emitters omit it).
                if (long.TryParse(hitsAttr, NumberStyles.Integer, CultureInfo.InvariantCulture, out var h))
                    hits = (int)Math.Clamp(h, int.MinValue, int.MaxValue);
                else
                    warnings.Add(new CoverageWarning(
                        CoverageWarningKind.MalformedHits,
                        filename,
                        lineNum,
                        $"hits='{hitsAttr}' could not be parsed - treating as 0"));
            }

            acc.LineHits[lineNum] = acc.LineHits.TryGetValue(lineNum, out var existing)
                ? Math.Max(existing, hits)
                : hits;

            // Cobertura emitters disagree on casing: original Cobertura/JaCoCo write
            // `branch="true"`, Coverlet writes `branch="True"` (XmlConvert.ToString(bool)).
            // A literal-pattern compare silently dropped Coverlet branches and rendered
            // branch coverage as a fake 100% (with TotalBranches=0).
            if (string.Equals(sub.GetAttribute("branch"), "true", StringComparison.OrdinalIgnoreCase) &&
                sub.GetAttribute("condition-coverage") is { } cond)
            {
                if (TryParseConditionCoverage(cond, out var covered, out var total))
                {
                    acc.BranchesByLine[lineNum] = acc.BranchesByLine.TryGetValue(lineNum, out var existingBranch)
                        ? (Math.Max(existingBranch.Covered, covered), Math.Max(existingBranch.Total, total))
                        : (covered, total);
                    conditionLine = lineNum;   // collect this branched line's <condition> children
                }
                else
                {
                    // Surface emitter regressions (malformed condition strings, overflow) as
                    // structured warnings instead of silently dropping the branch entry.
                    warnings.Add(new CoverageWarning(
                        CoverageWarningKind.MalformedConditionCoverage,
                        filename,
                        lineNum,
                        $"condition-coverage='{cond}' could not be parsed"));
                }
            }
        }
    }

    private static CoverageReport Materialize(
        Dictionary<string, LineAccumulator> files,
        List<CoverageWarning> warnings)
    {
        var result = new List<FileCoverage>(files.Count);
        foreach (var (filename, acc) in files)
        {
            // Keep per-condition detail only where it reconstructs the line aggregate as 2-outcome
            // jumps (the universal case for &&/||/?:/??/?.). If a switch jump-table makes it
            // inconsistent, drop to the line aggregate so merge never invents a total the emitter
            // didn't report — the invariant Merge's per-condition union relies on.
            var conditionsByLine = new Dictionary<int, IReadOnlyDictionary<int, int>>();
            foreach (var (line, conds) in acc.ConditionsByLine)
                // Every line in ConditionsByLine has a BranchesByLine entry by construction —
                // AddCondition only fires after the aggregate is recorded — so index directly
                // (a missing key would be a broken invariant worth throwing on, not silently skipping).
                if (conds.Count * 2 == acc.BranchesByLine[line].Total)
                    conditionsByLine[line] = new Dictionary<int, int>(conds);

            result.Add(FileCoverage.FromLineData(filename, acc.LineHits, acc.BranchesByLine, conditionsByLine));
        }

        return new CoverageReport(result) { Warnings = warnings };
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
        // Range-gate before rounding (NaN and ±Infinity fail the pattern too): a percent
        // outside [0,100] would put a covered value outside 0–2 into the per-condition map,
        // which a later merge recompute turns into BranchesHit > BranchesTotal.
        if (percent is not (>= 0 and <= 100)) return false;
        covered = (int)Math.Round(percent / 100.0 * 2.0, MidpointRounding.AwayFromZero);
        return true;
    }

    [GeneratedRegex(@"\((\d+)/(\d+)\)")]
    private static partial Regex ConditionPattern();
}
