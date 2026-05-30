using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;

namespace DotCov;

/// <summary>
/// Streaming Cobertura XML parser. Same pattern as AccessReportXml:
/// XmlReader cursor walks the document — XML never held in memory.
/// Secure: DtdProcessing.Prohibit, XmlResolver = null, character cap.
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

    public static CoverageReport ParseDirectory(string directory, string pattern = DefaultPattern)
    {
        var files = Directory.GetFiles(directory, Path.GetFileName(pattern),
            new EnumerationOptions { RecurseSubdirectories = pattern.Contains("**") });

        if (files.Length is 0)
            return CoverageReport.Empty;

        return files
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => ParseFile(f))
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
        DtdProcessing = DtdProcessing.Prohibit,
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

        while (sub.Read())
        {
            if (sub is not { NodeType: XmlNodeType.Element, LocalName: "line" })
                continue;

            if (!int.TryParse(sub.GetAttribute("number"), out var lineNum))
                continue;

            var hits = int.TryParse(sub.GetAttribute("hits"), out var h) ? h : 0;

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
            var linesHit = 0;
            var uncovered = new List<int>();
            foreach (var (line, hits) in acc.LineHits)
            {
                if (hits > 0) linesHit++;
                else uncovered.Add(line);
            }

            uncovered.Sort();

            var branchesHit = 0;
            var branchesTotal = 0;
            var partialBranches = new List<BranchDetail>();
            foreach (var (line, b) in acc.BranchesByLine.OrderBy(kv => kv.Key))
            {
                branchesHit += b.Covered;
                branchesTotal += b.Total;
                if (b.Covered < b.Total)
                    partialBranches.Add(new BranchDetail(line, b.Covered, b.Total));
            }

            var (strict, partial) = FileCoverage.ClassifyLines(acc.LineHits, acc.BranchesByLine);
            result.Add(new FileCoverage(filename, linesHit, acc.LineHits.Count, branchesHit, branchesTotal)
            {
                LineHits = acc.LineHits,
                BranchesByLine = acc.BranchesByLine,
                UncoveredLines = uncovered,
                PartialBranches = partialBranches,
                StrictlyHitLines = strict,
                PartiallyHitLines = partial
            });
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

    [GeneratedRegex(@"\((\d+)/(\d+)\)")]
    private static partial Regex ConditionPattern();
}
