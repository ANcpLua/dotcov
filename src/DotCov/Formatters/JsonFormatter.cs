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
            files = report.Files.Select(FormatFile),
            // Same `Count == 0 ? null : ...` shape as `lineChanges` — the
            // WhenWritingNull policy drops the property entirely when empty so consumers
            // can detect a clean report with `!root.TryGetProperty("warnings", out _)`.
            warnings = report.Warnings.Count > 0
                ? report.Warnings.Select(w => new
                {
                    kind = w.Kind.ToString(),
                    file = w.File,
                    line = w.Line,
                    detail = w.Detail
                })
                : null
        }, Options);

    public static string FormatDiff(CoverageDiffResult diff) =>
        JsonSerializer.Serialize(new
        {
            summary = new
            {
                before = Pct(diff.BeforeRate),
                after = Pct(diff.AfterRate),
                delta = Pct(diff.Delta),
                indirectLineChanges = diff.TotalLineChanges
            },
            files = diff.Files.Select(d => new
            {
                path = d.Path,
                before = d.Before.HasValue ? Pct(d.Before.Value) : (double?)null,
                after = d.After.HasValue ? Pct(d.After.Value) : (double?)null,
                delta = Pct(d.Delta),
                change = d.Change.ToString().ToLowerInvariant(),
                lineChanges = d.LineChanges.Count > 0
                    ? d.LineChanges.Select(c => new
                    {
                        line = c.Line,
                        beforeHits = c.BeforeHits,
                        afterHits = c.AfterHits,
                        change = c.Change.ToString().ToLowerInvariant()
                    })
                    : null
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
        branchRate = report.HasBranchData ? Pct(report.BranchRate) : (double?)null,
        hasBranchData = report.HasBranchData,
        totalLines = report.TotalLines,
        coveredLines = report.TotalLinesHit,
        totalBranches = report.TotalBranches,
        coveredBranches = report.TotalBranchesHit
    };

    private static object FormatFile(FileCoverage f) => new
    {
        path = f.Path,
        lineRate = Pct(f.LineRate),
        branchRate = f.HasBranchData ? Pct(f.BranchRate) : (double?)null,
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
