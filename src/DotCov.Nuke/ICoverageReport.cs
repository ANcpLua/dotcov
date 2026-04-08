using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Components;
using DotCov;
using DotCov.Formatters;

namespace DotCov.Nuke;

/// <summary>
/// NUKE build component for Cobertura coverage reporting.
/// Streaming parser — no DOM, no XDocument.Load, handles 50MB+ files.
///
/// Usage:
///   class Build : NukeBuild, ICoverageReport { }
///   nuke ReportCoverage --coverage-min-line 80
///
/// Loose dependency on ICompile — applies only when both are inherited.
/// </summary>
[ParameterPrefix("Coverage")]
public interface ICoverageReport : INukeBuild
{
    [Parameter("Minimum line coverage percentage")]
    string MinLine => TryGetValue(() => MinLine) ?? "80";

    [Parameter("Minimum branch coverage percentage")]
    string MinBranch => TryGetValue(() => MinBranch) ?? "0";

    [Parameter("Output format: table, json, markdown")]
    string Format => TryGetValue(() => Format) ?? "table";

    [Parameter("Exclude generated files, migrations, state machines")]
    string ExcludeGeneratedParam => TryGetValue(() => ExcludeGeneratedParam) ?? "false";

    bool ExcludeGenerated => bool.TryParse(ExcludeGeneratedParam, out var v) && v;

    AbsolutePath CoverageSearchDirectory => RootDirectory / "TestResults";

    Target ReportCoverage => d => d
        .Description("Parse Cobertura XML and report coverage. Fails if below threshold.")
        .TryDependsOn<ICompile>()
        .Executes(() =>
        {
            var coberturaFiles = CoverageSearchDirectory
                .GlobFiles("**/coverage.cobertura.xml")
                .ToList();

            Assert.NotEmpty(coberturaFiles,
                $"No coverage.cobertura.xml files found in {CoverageSearchDirectory}");

            var report = coberturaFiles
                .Select(f => CoberturaParser.ParseFile(f))
                .Aggregate(CoverageReport.Merge);

            if (ExcludeGenerated)
                report = report.Exclude(ExclusionRules.WellKnown);

            var minLine = double.Parse(MinLine);
            var minBranch = double.Parse(MinBranch);

            var output = Format switch
            {
                "json" => JsonFormatter.Format(report),
                "markdown" or "md" => MarkdownFormatter.Format(report, minLine),
                _ => TableFormatter.Format(report)
            };

            Serilog.Log.Information("Coverage:\n{Output}", output);

            WriteGitHubStepSummary(MarkdownFormatter.Format(report, minLine));

            Assert.True(
                report.MeetsThreshold(minLine, minBranch),
                $"Coverage below threshold: line {report.LineRate * 100:F1}% < {minLine}% " +
                $"or branch {report.BranchRate * 100:F1}% < {minBranch}%");
        });

    private static void WriteGitHubStepSummary(string markdown)
    {
        var path = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (path is null) return;
        File.AppendAllText(path, markdown);
    }
}
