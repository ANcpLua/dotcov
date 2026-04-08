using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotCov.Formatters;

public static class JsonFormatter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Format(CoverageReport report) =>
        JsonSerializer.Serialize(new
        {
            summary = FormatSummary(report),
            files = report.Files.Select(FormatFile)
        }, Options);

    public static string FormatDiff(CoverageDiffResult diff) =>
        JsonSerializer.Serialize(new
        {
            summary = new
            {
                before = Pct(diff.BeforeRate),
                after = Pct(diff.AfterRate),
                delta = Pct(diff.Delta)
            },
            files = diff.Files.Select(d => new
            {
                path = d.Path,
                before = d.Before.HasValue ? Pct(d.Before.Value) : (double?)null,
                after = d.After.HasValue ? Pct(d.After.Value) : (double?)null,
                delta = Pct(d.Delta),
                change = d.Change.ToString().ToLowerInvariant()
            })
        }, Options);

    public static string FormatSnapshot(CoverageSnapshot snapshot) =>
        JsonSerializer.Serialize(new
        {
            commit = snapshot.CommitSha,
            branch = snapshot.Branch,
            project = snapshot.Project,
            timestamp = snapshot.Timestamp,
            fileHash = snapshot.FileHash,
            summary = FormatSummary(snapshot.Report),
            files = snapshot.Report.Files.Select(FormatFile)
        }, Options);

    private static object FormatSummary(CoverageReport report) => new
    {
        lineRate = Pct(report.LineRate),
        branchRate = Pct(report.BranchRate),
        totalLines = report.TotalLines,
        coveredLines = report.TotalLinesHit,
        totalBranches = report.TotalBranches,
        coveredBranches = report.TotalBranchesHit
    };

    private static object FormatFile(FileCoverage f) => new
    {
        path = f.Path,
        lineRate = Pct(f.LineRate),
        branchRate = Pct(f.BranchRate),
        linesHit = f.LinesHit,
        linesTotal = f.LinesTotal,
        branchesHit = f.BranchesHit,
        branchesTotal = f.BranchesTotal,
        uncoveredLines = f.UncoveredLines.Count > 0 ? f.UncoveredLines : null,
        partialBranches = f.PartialBranches.Count > 0
            ? f.PartialBranches.Select(b => new { line = b.Line, covered = b.Covered, total = b.Total })
            : null
    };

    private static double Pct(double rate) => Math.Round(rate * 100, 2);
}
