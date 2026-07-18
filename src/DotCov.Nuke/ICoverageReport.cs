using System.Globalization;
using Nuke.Common;
using Nuke.Common.IO;
using Nuke.Components;
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

            Assert.True(double.TryParse(MinLine, NumberStyles.Float, CultureInfo.InvariantCulture, out var minLine),
                $"Invalid Coverage MinLine: '{MinLine}' (expected a number).");
            Assert.True(double.TryParse(MinBranch, NumberStyles.Float, CultureInfo.InvariantCulture, out var minBranch),
                $"Invalid Coverage MinBranch: '{MinBranch}' (expected a number).");

            var output = Format switch
            {
                "json" => JsonFormatter.Format(report),
                "markdown" or "md" => MarkdownFormatter.Format(report, minLine),
                _ => TableFormatter.Format(report)
            };

            Serilog.Log.Information("Coverage:\n{Output}", output);

            WriteGitHubStepSummary(MarkdownFormatter.Format(report, minLine));

            // POLICY (shell, not core - open question for the effectful pass): NoData and
            // Disabled currently fail the build alongside Fail, on the reasoning that a gate which
            // cannot verify must not vouch. Whether NUKE should instead warn is a build-semantics
            // decision; GateResult.Outcome distinguishes the cases whenever that is settled.
            var gate = report.Evaluate(minLine, minBranch);
            Assert.True(gate.IsPass, gate.ToString());
        });

    private static void WriteGitHubStepSummary(string markdown)
    {
        var path = Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");
        if (path is null) return;
        File.AppendAllText(path, markdown);
    }
}
