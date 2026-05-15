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
                    ? d.LineChanges.Select(FormatLineDelta)
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

    // Discriminates the closed LineDelta hierarchy via type-pattern chain. Wire format
    // pins all-lowercase `change` strings (`added`, `removed`, `newlyhit`, `newlymissed`)
    // — same shape downstream consumers parsed before the sealed-hierarchy refactor.
    //
    // `is`-chain instead of a `switch` expression: LineDelta's constructor is private so
    // the four nested sealed records are the only possible runtime types, but Roslyn
    // can't prove that on `abstract record` + private-ctor, so a `switch` would force an
    // unreachable `_ => throw` arm that we can't cover. The final cast in this chain is
    // total by construction.
    private static object FormatLineDelta(LineDelta c)
    {
        if (c is LineDelta.Added a)
            return new { line = a.Line, beforeHits = (int?)null, afterHits = (int?)a.AfterHits, change = "added" };
        if (c is LineDelta.Removed r)
            return new { line = r.Line, beforeHits = (int?)r.BeforeHits, afterHits = (int?)null, change = "removed" };
        if (c is LineDelta.NewlyHit h)
            return new { line = h.Line, beforeHits = (int?)h.BeforeHits, afterHits = (int?)h.AfterHits, change = "newlyhit" };
        var m = (LineDelta.NewlyMissed)c;
        return new { line = m.Line, beforeHits = (int?)m.BeforeHits, afterHits = (int?)m.AfterHits, change = "newlymissed" };
    }

    private static double Pct(double rate) => Math.Round(rate * 100, 2);
}
