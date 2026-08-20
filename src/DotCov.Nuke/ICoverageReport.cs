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

    [Parameter("Output format: table, json, markdown (md)")]
    string Format => TryGetValue(() => Format) ?? "table";

    [Parameter("Exclude generated files, migrations, state machines")]
    string ExcludeGeneratedParam => TryGetValue(() => ExcludeGeneratedParam) ?? "false";

    bool ExcludeGenerated => CoverageReportHelpers.ParseFlag(ExcludeGeneratedParam, "Coverage ExcludeGeneratedParam");

    [Parameter("Report file name pattern: 'filename' or '**/filename' (gcovr and coverage.py emit coverage.xml)")]
    string Pattern => TryGetValue(() => Pattern) ?? "**/coverage.cobertura.xml";

    [Parameter("Per-file XML character cap; 0 = no cap")]
    string MaxCharsParam => TryGetValue(() => MaxCharsParam) ?? "50000000";

    long MaxChars => CoverageReportHelpers.ParseMaxChars(MaxCharsParam, "Coverage MaxCharsParam");

    AbsolutePath CoverageSearchDirectory => RootDirectory / "TestResults";

    Target ReportCoverage => d => d
        .Description("Parse Cobertura XML and report coverage. Fails if below threshold.")
        .TryDependsOn<ICompile>()
        .Executes(() =>
        {
            var report = CoverageReportHelpers.LoadReport(CoverageSearchDirectory, Pattern, MaxChars);

            // LoadReport yields the CoverageReport.Empty singleton only when discovery matched
            // no files; a file that parsed to zero coverage is a distinct instance and flows to
            // the NoData gate below. The message names the configured pattern — hard-coding the
            // default file name would lie once --coverage-pattern is set.
            Assert.True(!ReferenceEquals(report, CoverageReport.Empty),
                $"No files matching '{Pattern}' found in {CoverageSearchDirectory}");

            if (ExcludeGenerated)
                report = report.Exclude(ExclusionRules.WellKnown);

            var minLine = CoverageReportHelpers.ParseThreshold(MinLine, "Coverage MinLine");
            var minBranch = CoverageReportHelpers.ParseThreshold(MinBranch, "Coverage MinBranch");

            var output = CoverageReportHelpers.ParseFormat(Format, "Coverage Format") switch
            {
                "json" => JsonFormatter.Format(report),
                "markdown" => MarkdownFormatter.Format(report, minLine),
                "table" => TableFormatter.Format(report),
                _ => throw new InvalidOperationException("Unreachable: ParseFormat returns canonical values only.")
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
        if (string.IsNullOrEmpty(path)) return;
        if (!CoverageReportHelpers.TryAppendGitHubStepSummary(path, markdown))
            Serilog.Log.Warning("Could not write GitHub step summary to {Path}", path);
    }
}
