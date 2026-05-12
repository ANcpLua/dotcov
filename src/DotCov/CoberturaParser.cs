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
    // To produce per-source-line coverage we collect into Dictionary<filename, Dictionary<line, hits>>
    // and take the highest hit count seen for any (file, line) pair.

    private sealed class LineAccumulator
    {
        public readonly Dictionary<int, int> LineHits = new();
        public int BranchesHit;
        public int BranchesTotal;
        public readonly List<BranchDetail> PartialBranches = new();
    }

    private static CoverageReport ParseCore(XmlReader reader)
    {
        var files = new Dictionary<string, LineAccumulator>(StringComparer.OrdinalIgnoreCase);

        while (reader.Read())
        {
            if (reader is not { NodeType: XmlNodeType.Element, LocalName: "class" })
                continue;

            ConsumeClass(reader, files);
        }

        return Materialize(files);
    }

    private static async Task<CoverageReport> ParseCoreAsync(XmlReader reader, CancellationToken ct)
    {
        var files = new Dictionary<string, LineAccumulator>(StringComparer.OrdinalIgnoreCase);

        while (await reader.ReadAsync())
        {
            ct.ThrowIfCancellationRequested();

            if (reader is not { NodeType: XmlNodeType.Element, LocalName: "class" })
                continue;

            ConsumeClass(reader, files);
        }

        return Materialize(files);
    }

    private static void ConsumeClass(XmlReader reader, Dictionary<string, LineAccumulator> files)
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

            if (sub.GetAttribute("branch") is "true" &&
                sub.GetAttribute("condition-coverage") is { } cond)
            {
                ParseCondition(cond, lineNum, ref acc.BranchesHit, ref acc.BranchesTotal, acc.PartialBranches);
            }
        }
    }

    private static CoverageReport Materialize(Dictionary<string, LineAccumulator> files)
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

            result.Add(new FileCoverage(filename, linesHit, acc.LineHits.Count, acc.BranchesHit, acc.BranchesTotal)
            {
                LineHits = acc.LineHits,
                UncoveredLines = uncovered,
                PartialBranches = acc.PartialBranches
            });
        }
        return new CoverageReport(result);
    }

    private static void ParseCondition(
        string cond, int lineNum, ref int hit, ref int total, List<BranchDetail> partials)
    {
        var match = ConditionPattern().Match(cond);
        if (!match.Success) return;

        if (!int.TryParse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture, out var covered)) return;
        if (!int.TryParse(match.Groups[2].ValueSpan, CultureInfo.InvariantCulture, out var branchTotal)) return;

        hit += covered;
        total += branchTotal;

        if (covered < branchTotal && lineNum > 0)
            partials.Add(new BranchDetail(lineNum, covered, branchTotal));
    }

    [GeneratedRegex(@"\((\d+)/(\d+)\)")]
    private static partial Regex ConditionPattern();
}
