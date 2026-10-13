using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

/// <summary>
/// Runs the minimal consuming build in <c>tests/DotCov.Fallout.TestBuild</c> as a real
/// Fallout process: parameter binding, the loose <c>ICompile</c> dependency, missing inputs,
/// NoData, parser warnings, the step summary, and exit codes are observed through the
/// framework, not through helper methods.
/// </summary>
[NotInParallel(ProcessState.Environment)]
[Category("Integration")]
[Timeout(30_000)]
public sealed class FalloutBuildTests : IDisposable
{
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-fallout-build-");

    public FalloutBuildTests() => _ws.CreateDirectory(".fallout");

    public void Dispose() => _ws.Dispose();

    private sealed record BuildRun(int ExitCode, string Output);

    private async Task<BuildRun> RunBuild(CancellationToken cancellationToken, IReadOnlyDictionary<string, string?>? environment = null, params string[] args)
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
        psi.Environment.Remove("DOTCOV_TESTBUILD_READY_PIPE");
        if (environment is not null)
            foreach (var (name, value) in environment)
                if (value is null) psi.Environment.Remove(name); else psi.Environment[name] = value;

        var run = await ChildProcess.RunAsync(psi, cancellationToken);
        return new BuildRun(run.ExitCode, AnsiStrip.From(run.Output));
    }

    private string WriteHalfCovered(string relative = "TestResults/run/coverage.cobertura.xml") =>
        _ws.Write(relative, Cobertura.NewDoc().AddClass("src/A.cs", c => c.Line(1, hits: 1).Line(2, hits: 0)));

    // ── Gate policy ───────────────────────────────────────────────────────────

    [Test]
    [Arguments("coverage.cobertura.xml")]
    [Arguments("dotcov.coverage.cobertura.170926234734218.xml")]
    public async Task Pass_ExitsZero_AndLogsTheVerdict(string reportName, CancellationToken cancellationToken)
    {
        WriteHalfCovered($"TestResults/run/{reportName}");

        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", "40");

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.Output);
        await Assert.That(run.Output).Contains("Coverage gate passed: PASS: line 50.0% (min 40%)");
        await Assert.That(run.Output).Contains("src/A.cs");   // the table was rendered
    }

    [Test]
    public async Task Fail_ExitsNonZero_WithTheThresholdMessage(CancellationToken cancellationToken)
    {
        WriteHalfCovered();

        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", "80");

        await Assert.That(run.ExitCode).IsNotEqualTo(0);
        await Assert.That(run.Output).Contains("Coverage below threshold: FAIL: line 50.0% (min 80%)");
    }

    [Test]
    public async Task NoData_ParsedReportWithoutMeasurements_ExitsNonZero_Distinctly(CancellationToken cancellationToken)
    {
        _ws.Write("TestResults/coverage.cobertura.xml", Cobertura.NewDoc());

        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", "80");

        await Assert.That(run.ExitCode).IsNotEqualTo(0);
        await Assert.That(run.Output).Contains("Coverage could not be measured: NODATA:");
        await Assert.That(run.Output).DoesNotContain("No files matching");
    }

    [Test]
    public async Task Disabled_EveryThresholdZero_ExitsNonZero_Distinctly(CancellationToken cancellationToken)
    {
        WriteHalfCovered();

        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", "0", "--coverage-min-branch", "0");

        await Assert.That(run.ExitCode).IsNotEqualTo(0);
        await Assert.That(run.Output).Contains("Coverage gate is disabled");
        await Assert.That(run.Output).Contains("DISABLED:");
    }

    // ── Inputs ────────────────────────────────────────────────────────────────

    [Test]
    public async Task MissingSearchDirectory_FailsNamingTheDirectory(CancellationToken cancellationToken)
    {
        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", "40");

        await Assert.That(run.ExitCode).IsNotEqualTo(0);
        await Assert.That(run.Output).Contains($"Coverage search directory '{_ws.PathOf("TestResults")}' does not exist");
    }

    [Test]
    public async Task DirectoryWithoutMatches_FailsNamingThePattern(CancellationToken cancellationToken)
    {
        _ws.Write("TestResults/notes.txt", "nothing here");

        var run = await RunBuild(cancellationToken, null, "--coverage-pattern", "**/coverage.xml", "--coverage-min-line", "40");

        await Assert.That(run.ExitCode).IsNotEqualTo(0);
        await Assert.That(run.Output).Contains($"No files matching '**/coverage.xml' found in {_ws.PathOf("TestResults")}");
    }

    [Test]
    public async Task Pattern_FindsNonCoverletNamesAndHiddenDirectories(CancellationToken cancellationToken)
    {
        _ws.Write("TestResults/.hidden/coverage.xml", Cobertura.NewDoc().AddClass("src/A.cs", c => c.Line(1, hits: 1)));

        var run = await RunBuild(cancellationToken, null, "--coverage-pattern", "**/coverage.xml", "--coverage-min-line", "50");

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.Output);
        await Assert.That(run.Output).Contains("PASS: line 100.0%");
    }

    [Test]
    public async Task MalformedReport_FailsNamingTheFile(CancellationToken cancellationToken)
    {
        var bad = _ws.Write("TestResults/coverage.cobertura.xml", "<coverage><packa");

        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", "40");

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
    public async Task InvalidParameter_FailsBeforeReadingAnyReport(string flag, string value, string expected, CancellationToken cancellationToken)
    {
        WriteHalfCovered();

        // A threshold in the dimension the case does not test: the named parameter is the only error.
        var run = await RunBuild(cancellationToken, null, flag, value, "--coverage-min-branch", "0");

        await Assert.That(run.ExitCode).IsNotEqualTo(0);
        await Assert.That(run.Output).Contains(expected);
        await Assert.That(run.Output).DoesNotContain("src/A.cs");
    }

    [Test]
    public async Task MaxChars_BelowDocumentSize_FailsNamingTheFile(CancellationToken cancellationToken)
    {
        var path = WriteHalfCovered();

        var run = await RunBuild(cancellationToken, null, "--coverage-max-chars-param", "50", "--coverage-min-line", "40");

        await Assert.That(run.ExitCode).IsNotEqualTo(0);
        await Assert.That(run.Output).Contains($"Could not parse coverage report {path}");
    }

    [Test]
    public async Task Format_MarkdownAlias_RendersMarkdownWithTheVerdict(CancellationToken cancellationToken)
    {
        WriteHalfCovered();

        var run = await RunBuild(cancellationToken, null, "--coverage-format", "md", "--coverage-min-line", "40");

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.Output);
        await Assert.That(run.Output).Contains("**Line coverage:** 50.0% (1/2)");
    }

    [Test]
    public async Task ExcludeGenerated_TrueDropsGeneratedFiles_FalseKeepsThem(CancellationToken cancellationToken)
    {
        _ws.Write("TestResults/coverage.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c.Line(1, hits: 1))
            .AddClass("src/Migrations/M1.cs", c => c.Line(1, hits: 0).Line(2, hits: 0)));

        var kept = await RunBuild(cancellationToken, null, "--coverage-min-line", "30");
        var excluded = await RunBuild(cancellationToken, null, "--coverage-min-line", "30", "--coverage-exclude-generated-param", "true");

        await Assert.That(kept.ExitCode).IsEqualTo(0).Because(kept.Output);
        await Assert.That(kept.Output).Contains("Migrations/M1.cs");
        await Assert.That(kept.Output).DoesNotContain("Excluded ");
        // Gone from the table; named once, in the exclusion line.
        await Assert.That(excluded.Output).Contains("Excluded 1 files by");
        await Assert.That(excluded.Output.Split("Migrations/M1.cs").Length - 1).IsEqualTo(1).Because(excluded.Output);
        await Assert.That(excluded.Output).Contains("PASS: line 100.0%");
    }

    [Test]
    public async Task ExcludeTestCode_TrueDropsTestCode_AndLogsWhatItExcluded(CancellationToken cancellationToken)
    {
        _ws.Write("TestResults/coverage.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c.Line(1, hits: 1).Line(2, hits: 0))
            .AddClass("tests/A.Tests/ATests.cs", c => c.Line(1, hits: 1).Line(2, hits: 1)));

        var kept = await RunBuild(cancellationToken, null, "--coverage-min-line", "70");
        var excluded = await RunBuild(cancellationToken, null, "--coverage-min-line", "70", "--coverage-exclude-test-code-param", "true");

        await Assert.That(kept.ExitCode).IsEqualTo(0).Because(kept.Output);
        await Assert.That(excluded.ExitCode).IsNotEqualTo(0);
        await Assert.That(excluded.Output).Contains("Coverage below threshold: FAIL: line 50.0% (min 70%)");
        await Assert.That(excluded.Output).Contains("Excluded 1 files by");
        await Assert.That(excluded.Output).Contains("tests/A.Tests/ATests.cs");
    }

    // ── Thresholds: explicit, 0 to 100, one disables the other ────────────────

    [Test]
    public async Task NoThreshold_FailsNamingTheParameters_BeforeReadingAnyReport(CancellationToken cancellationToken)
    {
        // A malformed report would fail the parse; the missing threshold fails first.
        _ws.Write("TestResults/coverage.cobertura.xml", "<coverage><packa");

        var run = await RunBuild(cancellationToken);

        await Assert.That(run.ExitCode).IsNotEqualTo(0);
        await Assert.That(run.Output).Contains("Missing Coverage MinLine and Coverage MinBranch: the coverage gate has no default threshold.");
        await Assert.That(run.Output).DoesNotContain("Could not parse");
    }

    [Test]
    [Arguments("--coverage-min-line", "150", "Invalid Coverage MinLine: '150' (expected a number from 0 to 100).")]
    [Arguments("--coverage-min-branch", "Infinity", "Invalid Coverage MinBranch: 'Infinity' (expected a number from 0 to 100).")]
    [Arguments("--coverage-min-line", "NaN", "Invalid Coverage MinLine: 'NaN' (expected a number from 0 to 100).")]
    public async Task ThresholdOutsideZeroToHundred_FailsNamingTheParameter(
        string flag, string value, string expected, CancellationToken cancellationToken)
    {
        WriteHalfCovered();

        var run = await RunBuild(cancellationToken, null, flag, value);

        await Assert.That(run.ExitCode).IsNotEqualTo(0);
        await Assert.That(run.Output).Contains(expected);
        await Assert.That(run.Output).DoesNotContain("src/A.cs");
    }

    [Test]
    public async Task BranchThresholdAlone_LeavesTheLineDimensionUnchecked(CancellationToken cancellationToken)
    {
        _ws.Write("TestResults/coverage.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/M.cs", c => c.Line(1, hits: 0).Branch(2, "100% (2/2)")));

        var run = await RunBuild(cancellationToken, null, "--coverage-min-branch", "90");

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.Output);
        await Assert.That(run.Output).Contains("Coverage gate passed: PASS: line 50.0% (min 0%), branch 100.0% (min 90%)");
    }

    // ── File identity: lexical by default, probing and an explicit root opt in ──

    private void WriteTwoRootReport() =>
        _ws.Write("TestResults/coverage.cobertura.xml", Cobertura.NewDoc()
            .WithSource("src").WithSource("tests")
            .AddClass("test_app.py", c => c.Line(1, hits: 1).Line(2, hits: 0)));

    [Test]
    public async Task AmbiguousIdentity_IsNoData_ByDefault(CancellationToken cancellationToken)
    {
        WriteTwoRootReport();
        _ws.Write("tests/test_app.py", "");   // on disk, but the default never looks

        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", "40");

        await Assert.That(run.ExitCode).IsNotEqualTo(0);
        await Assert.That(run.Output).Contains(
            "Coverage could not be measured: NODATA: line 50.0% (min 40%), branch n/a (min 0%) - 1 file has ambiguous identity across 2 source roots");
        await Assert.That(run.Output).Contains("relative filename 'test_app.py' could belong to any of 2 <source> roots");
    }

    [Test]
    [Arguments("--coverage-probe-sources-param", "true")]
    [Arguments("--coverage-source-root", "tests")]
    public async Task AmbiguousIdentity_ResolvedByEitherParameter_IsAMeasuredPass(
        string flag, string value, CancellationToken cancellationToken)
    {
        WriteTwoRootReport();
        _ws.Write("tests/test_app.py", "");

        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", "40", flag, value);

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.Output);
        await Assert.That(run.Output).Contains("Coverage gate passed: PASS: line 50.0% (min 40%)");
        await Assert.That(run.Output).Contains("tests/test_app.py");
    }

    [Test]
    public async Task SourceRoot_NotDeclared_FailsNamingTheParameter(CancellationToken cancellationToken)
    {
        WriteTwoRootReport();

        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", "40", "--coverage-source-root", "lib");

        await Assert.That(run.ExitCode).IsNotEqualTo(0);
        await Assert.That(run.Output).Contains("Invalid Coverage SourceRoot: source root 'lib' is not declared by ");
        await Assert.That(run.Output).DoesNotContain("PASS:");
    }

    [Test]
    public async Task ProbeSourcesAndSourceRoot_Together_FailBeforeReadingAnyReport(CancellationToken cancellationToken)
    {
        WriteTwoRootReport();

        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", "40",
            "--coverage-probe-sources-param", "true", "--coverage-source-root", "tests");

        await Assert.That(run.ExitCode).IsNotEqualTo(0);
        await Assert.That(run.Output).Contains("Coverage ProbeSourcesParam and Coverage SourceRoot are alternatives");
        await Assert.That(run.Output).DoesNotContain("test_app.py");
    }

    // ── Diagnostics and summary ───────────────────────────────────────────────

    [Test]
    public async Task ParserWarnings_AreLoggedCompletely_AndDoNotChangeTheVerdict(CancellationToken cancellationToken)
    {
        _ws.Write("TestResults/coverage.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c.MalformedLine("1", "lots").MalformedLine("2", "many").Line(3, hits: 1)));

        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", "30");

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.Output);
        await Assert.That(run.Output).Contains("src/A.cs:1: hits='lots' could not be parsed");
        await Assert.That(run.Output).Contains("src/A.cs:2: hits='many' could not be parsed");
    }

    [Test]
    public async Task StepSummary_IsWrittenOnPassAndFail_FromTheSameGate(CancellationToken cancellationToken)
    {
        WriteHalfCovered();
        var summary = _ws.PathOf("summary.md");
        var env = new Dictionary<string, string?> { ["GITHUB_STEP_SUMMARY"] = summary };

        var pass = await RunBuild(cancellationToken, env, "--coverage-min-line", "40");
        var passMarkdown = await File.ReadAllTextAsync(summary);
        File.Delete(summary);
        var fail = await RunBuild(cancellationToken, env, "--coverage-min-line", "80");
        var failMarkdown = await File.ReadAllTextAsync(summary);

        await Assert.That(pass.ExitCode).IsEqualTo(0).Because(pass.Output);
        await Assert.That(passMarkdown).Contains("PASS: line 50.0% (min 40%)");
        await Assert.That(fail.ExitCode).IsNotEqualTo(0);
        await Assert.That(failMarkdown).Contains("FAIL: line 50.0% (min 80%)");
    }

    [Test]
    public async Task StepSummary_UnwritableTarget_WarnsAndKeepsThePass(CancellationToken cancellationToken)
    {
        WriteHalfCovered();
        var env = new Dictionary<string, string?> { ["GITHUB_STEP_SUMMARY"] = _ws.PathOf("no-such-dir/summary.md") };

        var run = await RunBuild(cancellationToken, env, "--coverage-min-line", "40");

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.Output);
        await Assert.That(run.Output).Contains("Could not write GitHub step summary");
        await Assert.That(run.Output).Contains("Coverage gate passed");
    }

    // ── Badge ─────────────────────────────────────────────────────────────────

    [Test]
    [Arguments("40", false, true, "50.0%", "brightgreen")]
    [Arguments("80", false, false, "50.0%", "red")]
    [Arguments("0", false, false, "disabled", "yellow")]
    [Arguments("80", true, false, "no data", "lightgrey")]
    public async Task BadgeDirectory_WritesTheGateBadge_ForEveryOutcome(
        string minLine, bool empty, bool passes, string message, string color, CancellationToken cancellationToken)
    {
        if (empty) _ws.Write("TestResults/coverage.cobertura.xml", Cobertura.NewDoc()); else WriteHalfCovered();

        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", minLine, "--coverage-badge-directory", "out/badge");

        await Assert.That(run.ExitCode is 0).IsEqualTo(passes).Because(run.Output);
        await AssertBadge(_ws.PathOf("out/badge"), message, color);
    }

    [Test]
    public async Task BadgeDirectory_Absolute_IsUsedAsGiven(CancellationToken cancellationToken)
    {
        WriteHalfCovered();
        var directory = _ws.PathOf("elsewhere/badge");

        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", "40", "--coverage-badge-directory", directory);

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.Output);
        await AssertBadge(directory, "50.0%", "brightgreen");
    }

    [Test]
    public async Task BadgeDirectory_Unset_WritesNoBadge(CancellationToken cancellationToken)
    {
        WriteHalfCovered();

        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", "40");

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.Output);
        await Assert.That(Directory.EnumerateFiles(_ws.Root, "coverage-badge.*", SearchOption.AllDirectories)).IsEmpty();
    }

    [Test]
    public async Task BadgeDirectory_Unwritable_FailsAPassingGate(CancellationToken cancellationToken)
    {
        WriteHalfCovered();
        _ws.Write("blocked", "a file where the directory should go");

        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", "40", "--coverage-badge-directory", "blocked");

        await Assert.That(run.ExitCode).IsNotEqualTo(0);
        await Assert.That(run.Output).Contains("Coverage gate passed, but its badge could not be written:");
        await Assert.That(run.Output).DoesNotContain("Coverage gate passed: PASS");
    }

    [Test]
    public async Task BadgeDirectory_Unwritable_KeepsTheFailingVerdict(CancellationToken cancellationToken)
    {
        WriteHalfCovered();
        _ws.Write("blocked", "a file where the directory should go");

        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", "80", "--coverage-badge-directory", "blocked");

        await Assert.That(run.ExitCode).IsNotEqualTo(0);
        await Assert.That(run.Output).Contains("Could not write coverage badge to blocked:");
        await Assert.That(run.Output).Contains("Coverage below threshold: FAIL: line 50.0% (min 80%)");
    }

    private static async Task AssertBadge(string directory, string message, string color)
    {
        var svg = await File.ReadAllTextAsync(Path.Combine(directory, "coverage-badge.svg"));
        var json = await File.ReadAllTextAsync(Path.Combine(directory, "coverage-badge.json"));

        await Assert.That(svg).Contains($">{message}</text>");
        await Assert.That(json).IsEqualTo($$"""{"schemaVersion":1,"label":"Coverage","message":"{{message}}","color":"{{color}}"}""");
    }

    // ── Target graph ──────────────────────────────────────────────────────────

    [Test]
    public async Task WithoutICompile_ReportCoverageRunsAlone(CancellationToken cancellationToken)
    {
        WriteHalfCovered();

        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", "40");

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.Output);
        await Assert.That(run.Output).DoesNotContain("compile-target-ran");
        await Assert.That(run.Output).DoesNotContain("Compile");
    }

    [Test]
    public async Task WithICompile_CompileRunsBeforeReportCoverage(CancellationToken cancellationToken)
    {
        // ICompile requires a solution (IHasSolution.Solution is [Required]); Fallout reads it
        // from .fallout/parameters.json. A header-only .sln is enough for the loader.
        WriteHalfCovered();
        _ws.Write(".fallout/parameters.json", "{ \"Solution\": \"Test.sln\" }\n");
        _ws.Write("Test.sln", "\nMicrosoft Visual Studio Solution File, Format Version 12.00\n# Visual Studio Version 17\nVisualStudioVersion = 17.0.31903.59\nMinimumVisualStudioVersion = 10.0.40219.1\nGlobal\nEndGlobal\n");
        var env = new Dictionary<string, string?> { ["DOTCOV_TESTBUILD"] = "compile" };

        var run = await RunBuild(cancellationToken, env, "--coverage-min-line", "40");

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.Output);
        var compileAt = run.Output.IndexOf("compile-target-ran", StringComparison.Ordinal);
        var coverageAt = run.Output.IndexOf("Coverage gate passed", StringComparison.Ordinal);
        await Assert.That(compileAt).IsGreaterThanOrEqualTo(0).Because(run.Output);
        await Assert.That(coverageAt).IsGreaterThan(compileAt);
    }

    [Test]
    public async Task Cancellation_TerminatesBuildBeforeReturning(CancellationToken cancellationToken)
    {
        var pipeName = Guid.NewGuid().ToString("N");
        await using var ready = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var env = new Dictionary<string, string?>
        {
            ["DOTCOV_TESTBUILD"] = "wait",
            ["DOTCOV_TESTBUILD_READY_PIPE"] = pipeName,
        };
        var run = RunBuild(cancellation.Token, env);

        await ready.WaitForConnectionAsync(cancellationToken);
        using var reader = new StreamReader(ready);
        var pid = int.Parse((await reader.ReadLineAsync(cancellationToken))!, CultureInfo.InvariantCulture);
        using var child = Process.GetProcessById(pid);
        try
        {
            await cancellation.CancelAsync();

            await Assert.That(async () => await run.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken))
                .Throws<OperationCanceledException>();
            await Assert.That(child.HasExited).IsTrue();
        }
        finally
        {
            // Reap the child even when the runner failed to kill it.
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(CancellationToken.None);
        }
    }
}
