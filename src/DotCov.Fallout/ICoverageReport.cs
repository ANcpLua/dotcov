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
/// implements it and runs on its own otherwise. There is no default threshold: MinLine,
/// MinBranch, or both must be set, or the target fails before reading a report. The gate
/// policy is fail-closed: only <see cref="GateOutcome.Pass"/> succeeds; a failed, unmeasurable,
/// or disabled gate fails the target with a distinct message, and the build exits with
/// dotcov's own code for it (<see cref="ExitCodes"/>: 1 FAIL, 3 NODATA, 4 DISABLED, 2 for an
/// invalid parameter, 5 for an unreadable report or an unwritable badge or JSON file), so a
/// caller can tell them apart without reading the log.
/// </summary>
[ParameterPrefix("Coverage")]
public interface ICoverageReport : IFalloutBuild
{
    [Parameter("Minimum line coverage percentage, 0 to 100; set MinLine, MinBranch, or both")]
    string? MinLine => TryGetValue(() => MinLine);

    [Parameter("Minimum branch coverage percentage, 0 to 100; set MinLine, MinBranch, or both")]
    string? MinBranch => TryGetValue(() => MinBranch);

    [Parameter("Output format: table, json, markdown (md)")]
    string Format => TryGetValue(() => Format) ?? CoverageParameters.DefaultFormat;

    [Parameter("File, relative to the root, to also write the JSON report with its gate to, for every outcome; empty = none")]
    string JsonPath => TryGetValue(() => JsonPath) ?? CoverageParameters.DefaultJsonPath;

    [Parameter("Exclude generated files, migrations, GlobalUsings.cs, Program.cs (true/false)")]
    string ExcludeGeneratedParam => TryGetValue(() => ExcludeGeneratedParam) ?? CoverageParameters.DefaultExcludeGenerated;

    [Parameter("Exclude test projects and fixtures: *.Tests/, TestSupport/, TestFixtures/ (true/false)")]
    string ExcludeTestCodeParam => TryGetValue(() => ExcludeTestCodeParam) ?? CoverageParameters.DefaultExcludeTestCode;

    [Parameter("Resolve a relative filename under several <source> roots against the root that holds it on this machine's disk (true/false)")]
    string ProbeSourcesParam => TryGetValue(() => ProbeSourcesParam) ?? CoverageParameters.DefaultProbeSources;

    [Parameter("Resolve a relative filename under several <source> roots against this declared root")]
    string? SourceRoot => TryGetValue(() => SourceRoot);

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
            CoverageParameters parameters;
            try
            {
                parameters = CoverageParameters.Parse(
                    MinLine, MinBranch, Format, ExcludeGeneratedParam, ExcludeTestCodeParam, Pattern, MaxCharsParam,
                    ProbeSourcesParam, SourceRoot);
            }
            catch (ArgumentException)
            {
                ExitCode = ExitCodes.Usage;
                throw;
            }

            var searchDirectory = CoverageSearchDirectory.ToString();
            if (!Directory.Exists(searchDirectory))
                throw Failure(ExitCodes.Error, $"Coverage search directory '{searchDirectory}' does not exist");

            // Nothing to measure is NODATA, as check reads a directory without matches.
            var inputs = ReportResolver.ResolveDirectory(searchDirectory, parameters.Pattern);
            if (inputs.Count is 0)
                throw Failure(ExitCodes.NoData, $"No files matching '{parameters.Pattern}' found in {searchDirectory}");

            // Every match is merged, so a report an earlier run left under TestResults lifts the
            // result silently unless the merged set is named.
            if (inputs.Count > 1)
                Log.Information("Merging {Count} coverage reports: {Reports}",
                    inputs.Count, inputs.Select(static input => input.SourceName));

            var report = ParseReports(inputs, parameters.MaxChars, parameters.Identity)
                .Exclude(parameters.ExclusionPatterns);

            foreach (var warning in report.Warnings)
                Log.Warning("Coverage warning ({Kind}) {File}:{Line}: {Detail}",
                    warning.Kind, warning.File, warning.Line, warning.Detail);

            // A number computed without some files says which, like the CLI's stderr listing.
            if (report.Exclusion.IsActive)
                Log.Information("Excluded {Count} files by {Rules}: {Files}",
                    report.Exclusion.ExcludedFiles.Count, report.Exclusion.Patterns, report.Exclusion.ExcludedFiles);

            var gate = report.Evaluate(parameters.MinLine, parameters.MinBranch);

            // Rendered at most once each: the markdown serves both the terminal (when selected)
            // and the step summary, the JSON both the terminal and the JsonPath file. The JSON is
            // check's document, gate and offenders included.
            var markdown = new Lazy<string>(() => MarkdownFormatter.Format(report, gate));
            var json = new Lazy<string>(() => JsonFormatter.Format(report, gate, "check"));
            var output = parameters.Format switch
            {
                CoverageFormat.Json => json.Value,
                CoverageFormat.Markdown => markdown.Value,
                // Rendered from the gate below the table, so a failing dimension floors like the
                // verdict: never TOTAL 100.0% beside "FAIL: line 99.9% (min 100%)". A failed gate
                // lists what to fix next under it.
                _ => TableFormatter.Format(report, gate) + TableFormatter.FormatOffenders(report, gate)
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

            // Like the badge: written for every outcome, and a pass that cannot write it fails.
            string? jsonError = null;
            if (JsonPath is { Length: > 0 } jsonPath &&
                !CoverageJsonFile.TryWrite(Path.GetFullPath(jsonPath, RootDirectory), json.Value, out jsonError))
                Log.Error("Could not write coverage JSON to {Path}: {Error}", jsonPath, jsonError);

            switch (gate.Outcome)
            {
                case GateOutcome.Pass when badgeError is not null:
                    throw Failure(ExitCodes.Error, $"Coverage gate passed, but its badge could not be written: {badgeError}");
                case GateOutcome.Pass when jsonError is not null:
                    throw Failure(ExitCodes.Error, $"Coverage gate passed, but its JSON report could not be written: {jsonError}");
                case GateOutcome.Pass:
                    Log.Information("Coverage gate passed: {Gate}", gate.ToString());
                    break;
                case GateOutcome.Fail:
                    throw Failure(ExitCodes.Fail, $"Coverage below threshold: {gate}");
                case GateOutcome.NoData:
                    throw Failure(ExitCodes.NoData, $"Coverage could not be measured: {gate}");
                default:
                    throw Failure(ExitCodes.Disabled, $"Coverage gate is disabled - every threshold is 0, so nothing was verified: {gate}");
            }
        });

    /// <summary>
    /// What fails the target, as Assert.Fail would throw it, after recording dotcov's exit code
    /// for the reason: Fallout returns a build's <see cref="IFalloutBuild.ExitCode"/> instead of
    /// its generic failure code once it is set.
    /// </summary>
    private Exception Failure(int exitCode, string message, Exception? cause = null)
    {
        ExitCode = exitCode;
        return new Exception(message, cause);
    }

    /// <summary>
    /// Parse at the target boundary: a malformed report fails the target naming the file and XML
    /// coordinates (exit 5), and a SourceRoot no report declares fails naming the parameter
    /// (exit 2, a parameter that cannot be honored).
    /// </summary>
    private CoverageReport ParseReports(IReadOnlyList<ReportInput> inputs, long maxChars, FileIdentity identity)
    {
        try
        {
            return CoberturaParser.Parse(inputs, maxChars, identity);
        }
        catch (ReportParseException ex)
        {
            throw Failure(ExitCodes.Error,
                $"Could not parse coverage report {ex.SourceName} (line {ex.LineNumber}, position {ex.LinePosition}): {ex.Message}", ex);
        }
        catch (UndeclaredSourceRootException ex)
        {
            throw Failure(ExitCodes.Usage, $"Invalid Coverage SourceRoot: {ex.Message}", ex);
        }
    }
}
