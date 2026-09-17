using System.Diagnostics;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

/// <summary>
/// Runs the minimal consuming build in <c>tests/DotCov.Fallout.TestBuild</c> as a real
/// Fallout process: parameter binding, the loose <c>ICompile</c> dependency, missing inputs,
/// NoData, parser warnings, the step summary, and exit codes are observed through the
/// framework, not through helper methods.
/// </summary>
[NotInParallel(ProcessState.Environment)]
public sealed class FalloutBuildTests : IDisposable
{
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-fallout-build-");

    public FalloutBuildTests() => _ws.CreateDirectory(".fallout");

    public void Dispose() => _ws.Dispose();

    private sealed record BuildRun(int ExitCode, string Output);

    private async Task<BuildRun> RunBuild(IReadOnlyDictionary<string, string?>? environment = null, params string[] args)
    {
        var testsDir = AppContext.BaseDirectory;
        var buildDll = Path.Combine(testsDir, "DotCov.Fallout.TestBuild.dll");
        var depsFile = Path.Combine(testsDir, "DotCov.Tests.deps.json");
        var runtimeConfig = Path.Combine(testsDir, "DotCov.Tests.runtimeconfig.json");

        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _ws.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in new[] { "exec", "--depsfile", depsFile, "--runtimeconfig", runtimeConfig, buildDll, "ReportCoverage", "--root", _ws.Root, "--no-logo" })
            psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);

        psi.Environment["NO_COLOR"] = "1";
        psi.Environment.Remove("GITHUB_STEP_SUMMARY");
        psi.Environment.Remove("DOTCOV_TESTBUILD");
        if (environment is not null)
            foreach (var (name, value) in environment)
                if (value is null) psi.Environment.Remove(name); else psi.Environment[name] = value;

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new BuildRun(process.ExitCode, AnsiStrip.From(await stdout + await stderr));
    }

    private string WriteHalfCovered(string relative = "TestResults/run/coverage.cobertura.xml") =>
        _ws.Write(relative, Cobertura.NewDoc().AddClass("src/A.cs", c => c.Line(1, hits: 1).Line(2, hits: 0)));

    // ── Gate policy ───────────────────────────────────────────────────────────

    [Test]
    public async Task Pass_ExitsZero_AndLogsTheVerdict()
    {
        WriteHalfCovered();

        var run = await RunBuild(null, "--coverage-min-line", "40");

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.Output);
        await Assert.That(run.Output).Contains("Coverage gate passed: PASS: line 50.0% (min 40%)");
        await Assert.That(run.Output).Contains("src/A.cs");   // the table was rendered
    }

    [Test]
    public async Task Fail_ExitsNonZero_WithTheThresholdMessage()
    {
        WriteHalfCovered();

        var run = await RunBuild(null, "--coverage-min-line", "80");

        await Assert.That(run.ExitCode).IsNotEqualTo(0);
        await Assert.That(run.Output).Contains("Coverage below threshold: FAIL: line 50.0% (min 80%)");
    }

    [Test]
    public async Task NoData_ParsedReportWithoutMeasurements_ExitsNonZero_Distinctly()
    {
        _ws.Write("TestResults/coverage.cobertura.xml", Cobertura.NewDoc());

        var run = await RunBuild(null, "--coverage-min-line", "80");

        await Assert.That(run.ExitCode).IsNotEqualTo(0);
        await Assert.That(run.Output).Contains("Coverage could not be measured: NODATA:");
        await Assert.That(run.Output).DoesNotContain("No files matching");
    }

    [Test]
    public async Task Disabled_EveryThresholdZero_ExitsNonZero_Distinctly()
    {
        WriteHalfCovered();

        var run = await RunBuild(null, "--coverage-min-line", "0", "--coverage-min-branch", "0");

        await Assert.That(run.ExitCode).IsNotEqualTo(0);
        await Assert.That(run.Output).Contains("Coverage gate is disabled");
        await Assert.That(run.Output).Contains("DISABLED:");
    }

    // ── Inputs ────────────────────────────────────────────────────────────────

    [Test]
    public async Task MissingSearchDirectory_FailsNamingTheDirectory()
    {
        var run = await RunBuild();

        await Assert.That(run.ExitCode).IsNotEqualTo(0);
        await Assert.That(run.Output).Contains($"Coverage search directory '{_ws.PathOf("TestResults")}' does not exist");
    }

    [Test]
    public async Task DirectoryWithoutMatches_FailsNamingThePattern()
    {
        _ws.Write("TestResults/notes.txt", "nothing here");

        var run = await RunBuild(null, "--coverage-pattern", "**/coverage.xml");

        await Assert.That(run.ExitCode).IsNotEqualTo(0);
        await Assert.That(run.Output).Contains($"No files matching '**/coverage.xml' found in {_ws.PathOf("TestResults")}");
    }

    [Test]
    public async Task Pattern_FindsNonCoverletNamesAndHiddenDirectories()
    {
        _ws.Write("TestResults/.hidden/coverage.xml", Cobertura.NewDoc().AddClass("src/A.cs", c => c.Line(1, hits: 1)));

        var run = await RunBuild(null, "--coverage-pattern", "**/coverage.xml", "--coverage-min-line", "50");

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.Output);
        await Assert.That(run.Output).Contains("PASS: line 100.0%");
    }

    [Test]
    public async Task MalformedReport_FailsNamingTheFile()
    {
        var bad = _ws.Write("TestResults/coverage.cobertura.xml", "<coverage><packa");

        var run = await RunBuild();

        await Assert.That(run.ExitCode).IsNotEqualTo(0);
        await Assert.That(run.Output).Contains($"Could not parse coverage report {bad} (line 1, position ");
    }

    // ── Parameters ────────────────────────────────────────────────────────────

    [Test]
    [Arguments("--coverage-min-line", "eighty", "Invalid Coverage MinLine: 'eighty'")]
    [Arguments("--coverage-min-line", "80,5", "Invalid Coverage MinLine: '80,5'")]
    [Arguments("--coverage-exclude-generated-param", "yes", "Invalid Coverage ExcludeGeneratedParam: 'yes'")]
    [Arguments("--coverage-format", "xml", "Invalid Coverage Format: 'xml'")]
    [Arguments("--coverage-pattern", "cov/*.xml", "Invalid Coverage Pattern: 'cov/*.xml'")]
    // A leading '-' is consumed by Fallout's own argument grammar before the value reaches the
    // component, so the digits-only rejection of "-1" is pinned in CoverageParametersTests.
    [Arguments("--coverage-max-chars-param", "ten", "Invalid Coverage MaxCharsParam: 'ten'")]
    public async Task InvalidParameter_FailsBeforeReadingAnyReport(string flag, string value, string expected)
    {
        WriteHalfCovered();

        var run = await RunBuild(null, flag, value);

        await Assert.That(run.ExitCode).IsNotEqualTo(0);
        await Assert.That(run.Output).Contains(expected);
        await Assert.That(run.Output).DoesNotContain("src/A.cs");
    }

    [Test]
    public async Task MaxChars_BelowDocumentSize_FailsNamingTheFile()
    {
        var path = WriteHalfCovered();

        var run = await RunBuild(null, "--coverage-max-chars-param", "50");

        await Assert.That(run.ExitCode).IsNotEqualTo(0);
        await Assert.That(run.Output).Contains($"Could not parse coverage report {path}");
    }

    [Test]
    public async Task Format_MarkdownAlias_RendersMarkdownWithTheVerdict()
    {
        WriteHalfCovered();

        var run = await RunBuild(null, "--coverage-format", "md", "--coverage-min-line", "40");

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.Output);
        await Assert.That(run.Output).Contains("**Line coverage:** 50.0% (1/2)");
    }

    [Test]
    public async Task ExcludeGenerated_TrueDropsGeneratedFiles_FalseKeepsThem()
    {
        _ws.Write("TestResults/coverage.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c.Line(1, hits: 1))
            .AddClass("src/Migrations/M1.cs", c => c.Line(1, hits: 0).Line(2, hits: 0)));

        var kept = await RunBuild(null, "--coverage-min-line", "30");
        var excluded = await RunBuild(null, "--coverage-min-line", "30", "--coverage-exclude-generated-param", "true");

        await Assert.That(kept.ExitCode).IsEqualTo(0).Because(kept.Output);
        await Assert.That(kept.Output).Contains("Migrations/M1.cs");
        await Assert.That(excluded.Output).DoesNotContain("Migrations/M1.cs");
        await Assert.That(excluded.Output).Contains("PASS: line 100.0%");
    }

    // ── Diagnostics and summary ───────────────────────────────────────────────

    [Test]
    public async Task ParserWarnings_AreLoggedCompletely_AndDoNotChangeTheVerdict()
    {
        _ws.Write("TestResults/coverage.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c.MalformedLine("1", "lots").MalformedLine("2", "many").Line(3, hits: 1)));

        var run = await RunBuild(null, "--coverage-min-line", "30");

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.Output);
        await Assert.That(run.Output).Contains("src/A.cs:1: hits='lots' could not be parsed");
        await Assert.That(run.Output).Contains("src/A.cs:2: hits='many' could not be parsed");
    }

    [Test]
    public async Task StepSummary_IsWrittenOnPassAndFail_FromTheSameGate()
    {
        WriteHalfCovered();
        var summary = _ws.PathOf("summary.md");
        var env = new Dictionary<string, string?> { ["GITHUB_STEP_SUMMARY"] = summary };

        var pass = await RunBuild(env, "--coverage-min-line", "40");
        var passMarkdown = File.ReadAllText(summary);
        File.Delete(summary);
        var fail = await RunBuild(env, "--coverage-min-line", "80");
        var failMarkdown = File.ReadAllText(summary);

        await Assert.That(pass.ExitCode).IsEqualTo(0).Because(pass.Output);
        await Assert.That(passMarkdown).Contains("PASS: line 50.0% (min 40%)");
        await Assert.That(fail.ExitCode).IsNotEqualTo(0);
        await Assert.That(failMarkdown).Contains("FAIL: line 50.0% (min 80%)");
    }

    [Test]
    public async Task StepSummary_UnwritableTarget_WarnsAndKeepsThePass()
    {
        WriteHalfCovered();
        var env = new Dictionary<string, string?> { ["GITHUB_STEP_SUMMARY"] = _ws.PathOf("no-such-dir/summary.md") };

        var run = await RunBuild(env, "--coverage-min-line", "40");

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.Output);
        await Assert.That(run.Output).Contains("Could not write GitHub step summary");
        await Assert.That(run.Output).Contains("Coverage gate passed");
    }

    // ── Target graph ──────────────────────────────────────────────────────────

    [Test]
    public async Task WithoutICompile_ReportCoverageRunsAlone()
    {
        WriteHalfCovered();

        var run = await RunBuild(null, "--coverage-min-line", "40");

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.Output);
        await Assert.That(run.Output).DoesNotContain("compile-target-ran");
        await Assert.That(run.Output).DoesNotContain("Compile");
    }

    [Test]
    public async Task WithICompile_CompileRunsBeforeReportCoverage()
    {
        // ICompile requires a solution (IHasSolution.Solution is [Required]); Fallout reads it
        // from .fallout/parameters.json. A header-only .sln is enough for the loader.
        WriteHalfCovered();
        _ws.Write(".fallout/parameters.json", "{ \"Solution\": \"Test.sln\" }\n");
        _ws.Write("Test.sln", "\nMicrosoft Visual Studio Solution File, Format Version 12.00\n# Visual Studio Version 17\nVisualStudioVersion = 17.0.31903.59\nMinimumVisualStudioVersion = 10.0.40219.1\nGlobal\nEndGlobal\n");
        var env = new Dictionary<string, string?> { ["DOTCOV_TESTBUILD"] = "compile" };

        var run = await RunBuild(env, "--coverage-min-line", "40");

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.Output);
        var compileAt = run.Output.IndexOf("compile-target-ran", StringComparison.Ordinal);
        var coverageAt = run.Output.IndexOf("Coverage gate passed", StringComparison.Ordinal);
        await Assert.That(compileAt).IsGreaterThanOrEqualTo(0).Because(run.Output);
        await Assert.That(coverageAt).IsGreaterThan(compileAt);
    }
}
