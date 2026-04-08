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

    private static CoverageReport ParseCore(XmlReader reader)
    {
        var files = new List<FileCoverage>();

        while (reader.Read())
        {
            if (reader is not { NodeType: XmlNodeType.Element, LocalName: "class" })
                continue;

            if (ParseClass(reader) is { } coverage)
                files.Add(coverage);
        }

        return new CoverageReport(files);
    }

    private static async Task<CoverageReport> ParseCoreAsync(XmlReader reader, CancellationToken ct)
    {
        var files = new List<FileCoverage>();

        while (await reader.ReadAsync())
        {
            ct.ThrowIfCancellationRequested();

            if (reader is not { NodeType: XmlNodeType.Element, LocalName: "class" })
                continue;

            if (ParseClass(reader) is { } coverage)
                files.Add(coverage);
        }

        return new CoverageReport(files);
    }

    private static FileCoverage? ParseClass(XmlReader reader)
    {
        var filename = reader.GetAttribute("filename");
        if (filename is null) return null;

        int linesHit = 0, linesTotal = 0, branchesHit = 0, branchesTotal = 0;
        var uncoveredLines = new List<int>();
        var partialBranches = new List<BranchDetail>();

        if (reader.ReadToDescendant("line"))
        {
            do
            {
                linesTotal++;
                var lineNum = int.TryParse(reader.GetAttribute("number"), out var n) ? n : 0;
                var hits = 0;

                if (int.TryParse(reader.GetAttribute("hits"), out var h))
                    hits = h;

                if (hits > 0)
                    linesHit++;
                else if (lineNum > 0)
                    uncoveredLines.Add(lineNum);

                if (reader.GetAttribute("branch") is "true" &&
                    reader.GetAttribute("condition-coverage") is { } cond)
                {
                    ParseCondition(cond, lineNum, ref branchesHit, ref branchesTotal, partialBranches);
                }
            } while (reader.ReadToNextSibling("line"));
        }

        return new FileCoverage(filename, linesHit, linesTotal, branchesHit, branchesTotal)
        {
            UncoveredLines = uncoveredLines,
            PartialBranches = partialBranches
        };
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
