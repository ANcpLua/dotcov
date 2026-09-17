using DotCov.Formatters;
using Fallout.Common;
using Fallout.Common.IO;
using Fallout.Components;
using Serilog;

namespace DotCov.Fallout;

/// <summary>
/// Fallout build component for Cobertura coverage reporting.
///
/// Usage:
///   class Build : FalloutBuild, ICoverageReport { }
///   fallout ReportCoverage --coverage-min-line 80
///
/// <see cref="ReportCoverage"/> attaches itself to <see cref="ICompile"/> when the build
/// implements it and runs on its own otherwise. The gate policy is fail-closed: only
/// <see cref="GateOutcome.Pass"/> succeeds; a failed, unmeasurable, or disabled gate fails
/// the target with a distinct message.
/// </summary>
[ParameterPrefix("Coverage")]
public interface ICoverageReport : IFalloutBuild
{
    [Parameter("Minimum line coverage percentage")]
    string MinLine => TryGetValue(() => MinLine) ?? CoverageParameters.DefaultMinLine;

    [Parameter("Minimum branch coverage percentage")]
    string MinBranch => TryGetValue(() => MinBranch) ?? CoverageParameters.DefaultMinBranch;

    [Parameter("Output format: table, json, markdown (md)")]
    string Format => TryGetValue(() => Format) ?? CoverageParameters.DefaultFormat;

    [Parameter("Exclude generated files, migrations, state machines (true/false)")]
    string ExcludeGeneratedParam => TryGetValue(() => ExcludeGeneratedParam) ?? CoverageParameters.DefaultExcludeGenerated;

    [Parameter("Report file name pattern: 'filename' or '**/filename' (gcovr and coverage.py emit coverage.xml)")]
    string Pattern => TryGetValue(() => Pattern) ?? CoverageParameters.DefaultPattern;

    [Parameter("Per-file XML character cap; 0 = no cap")]
    string MaxCharsParam => TryGetValue(() => MaxCharsParam) ?? CoverageParameters.DefaultMaxChars;

    AbsolutePath CoverageSearchDirectory => RootDirectory / "TestResults";

    Target ReportCoverage => d => d
        .Description("Parse Cobertura XML and report coverage. Fails if below threshold.")
        .TryDependsOn<ICompile>()
        .Executes(() =>
        {
            var parameters = CoverageParameters.Parse(
                MinLine, MinBranch, Format, ExcludeGeneratedParam, Pattern, MaxCharsParam);

            var searchDirectory = CoverageSearchDirectory.ToString();
            Assert.True(Directory.Exists(searchDirectory),
                $"Coverage search directory '{searchDirectory}' does not exist");

            var inputs = ReportResolver.ResolveDirectory(searchDirectory, parameters.Pattern);
            Assert.True(inputs.Count > 0,
                $"No files matching '{parameters.Pattern}' found in {searchDirectory}");

            var report = ParseReports(inputs, parameters.MaxChars);
            if (parameters.ExcludeGenerated)
                report = report.Exclude(ExclusionRules.WellKnown);

            foreach (var warning in report.Warnings)
                Log.Warning("Coverage warning ({Kind}) {File}:{Line}: {Detail}",
                    warning.Kind, warning.File, warning.Line, warning.Detail);

            var gate = report.Evaluate(parameters.MinLine, parameters.MinBranch);

            // Rendered at most once: the markdown serves both the terminal (when selected)
            // and the step summary.
            var markdown = new Lazy<string>(() => MarkdownFormatter.Format(report, gate));
            var output = parameters.Format switch
            {
                CoverageFormat.Json => JsonFormatter.Format(report),
                CoverageFormat.Markdown => markdown.Value,
                _ => TableFormatter.Format(report)
            };
            Log.Information("Coverage:\n{Output}", output);

            if (GitHubStepSummary.ConfiguredPath is { } summaryPath &&
                !GitHubStepSummary.TryAppend(summaryPath, markdown.Value))
                Log.Warning("Could not write GitHub step summary to {Path}", summaryPath);

            switch (gate.Outcome)
            {
                case GateOutcome.Pass:
                    Log.Information("Coverage gate passed: {Gate}", gate.ToString());
                    break;
                case GateOutcome.Fail:
                    Assert.Fail($"Coverage below threshold: {gate}");
                    break;
                case GateOutcome.NoData:
                    Assert.Fail($"Coverage could not be measured: {gate}");
                    break;
                default:
                    Assert.Fail($"Coverage gate is disabled - every threshold is 0, so nothing was verified: {gate}");
                    break;
            }
        });

    /// <summary>Parse at the target boundary: a malformed report fails the target naming the file and XML coordinates.</summary>
    private static CoverageReport ParseReports(IReadOnlyList<ReportInput> inputs, long maxChars)
    {
        try
        {
            return CoberturaParser.Parse(inputs, maxChars);
        }
        catch (ReportParseException ex)
        {
            Assert.Fail($"Could not parse coverage report {ex.SourceName} (line {ex.LineNumber}, position {ex.LinePosition}): {ex.Message}", ex);
            throw;   // unreachable: Assert.Fail throws
        }
    }
}
