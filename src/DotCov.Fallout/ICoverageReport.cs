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

    [Parameter("Exclude generated files, migrations, GlobalUsings.cs, Program.cs (true/false)")]
    string ExcludeGeneratedParam => TryGetValue(() => ExcludeGeneratedParam) ?? CoverageParameters.DefaultExcludeGenerated;

    [Parameter("Report file name pattern: 'filename' or '**/filename' (gcovr and coverage.py emit coverage.xml)")]
    string Pattern => TryGetValue(() => Pattern) ?? CoverageParameters.DefaultPattern;

    [Parameter("Per-file XML character cap; 0 = no cap")]
    string MaxCharsParam => TryGetValue(() => MaxCharsParam) ?? CoverageParameters.DefaultMaxChars;

    [Parameter("Directory, relative to the root, to write coverage-badge.svg and coverage-badge.json to; empty = no badge")]
    string BadgeDirectory => TryGetValue(() => BadgeDirectory) ?? CoverageParameters.DefaultBadgeDirectory;

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

            // Every match is merged, so a report an earlier run left under TestResults lifts the
            // result silently unless the merged set is named.
            if (inputs.Count > 1)
                Log.Information("Merging {Count} coverage reports: {Reports}",
                    inputs.Count, inputs.Select(static input => input.SourceName));

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
                // Rendered from the gate below the table, so a failing dimension floors like the
                // verdict: never TOTAL 100.0% beside "FAIL: line 99.9% (min 100%)".
                _ => TableFormatter.Format(report, gate)
            };
            Log.Information("Coverage:\n{Output}", output);

            if (GitHubStepSummary.ConfiguredPath is { } summaryPath &&
                !GitHubStepSummary.TryAppend(summaryPath, markdown.Value))
                Log.Warning("Could not write GitHub step summary to {Path}", summaryPath);

            // Written for every outcome before the verdict can fail the target, so a failing gate
            // replaces a green badge. Unlike the summary, a badge is the verdict: a pass that
            // cannot write it fails, as in the CLI, rather than leave the previous badge standing.
            string? badgeError = null;
            if (BadgeDirectory is { Length: > 0 } badgeDirectory &&
                !CoverageBadge.TryWrite(Path.GetFullPath(badgeDirectory, RootDirectory), gate, out badgeError))
                Log.Error("Could not write coverage badge to {Directory}: {Error}", badgeDirectory, badgeError);

            switch (gate.Outcome)
            {
                case GateOutcome.Pass when badgeError is not null:
                    Assert.Fail($"Coverage gate passed, but its badge could not be written: {badgeError}");
                    break;
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
        CoverageReport? report = null;
        ReportParseException? failure = null;
        try
        {
            report = CoberturaParser.Parse(inputs, maxChars);
        }
        catch (ReportParseException ex)
        {
            failure = ex;
        }

        // Failed outside the catch block, so no statement has to follow Assert.Fail, which
        // throws: only a successful parse reaches the return.
        if (failure is not null)
            Assert.Fail($"Could not parse coverage report {failure.SourceName} (line {failure.LineNumber}, position {failure.LinePosition}): {failure.Message}", failure);

        return report!;
    }
}
