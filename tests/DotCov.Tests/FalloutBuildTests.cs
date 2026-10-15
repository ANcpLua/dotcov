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
[TestClass]
[ResourceLock(ProcessState.Environment)]
[TestCategory("Integration")]
public sealed class FalloutBuildTests : IDisposable
{
    private readonly TestContext _testContext;
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-fallout-build-");
    public FalloutBuildTests(TestContext testContext)
    {
        _testContext = testContext;
        _ws.CreateDirectory(".fallout");
    }

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
        foreach (var a in new[]
        {
            "exec",
            "--depsfile",
            depsFile,
            "--runtimeconfig",
            runtimeConfig,
            buildDll,
            "ReportCoverage",
            "--root",
            _ws.Root,
            "--no-logo"
        }

        )
            psi.ArgumentList.Add(a);
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment.Remove("GITHUB_STEP_SUMMARY");
        psi.Environment.Remove("DOTCOV_TESTBUILD");
        psi.Environment.Remove("DOTCOV_TESTBUILD_READY_PIPE");
        if (environment is not null)
            foreach (var(name, value)in environment)
                if (value is null)
                    psi.Environment.Remove(name);
                else
                    psi.Environment[name] = value;
        var run = await ChildProcess.RunAsync(psi, cancellationToken);
        return new BuildRun(run.ExitCode, AnsiStrip.From(run.Output));
    }

    private string WriteHalfCovered(string relative = "TestResults/run/coverage.cobertura.xml") => _ws.Write(relative, Cobertura.NewDoc().AddClass("src/A.cs", c => c.Line(1, hits: 1).Line(2, hits: 0)));
    // ── Gate policy ───────────────────────────────────────────────────────────
    [TestMethod]
    [DataRow("coverage.cobertura.xml")]
    [DataRow("dotcov.coverage.cobertura.170926234734218.xml")]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Pass_ExitsZero_AndLogsTheVerdict(string reportName)
    {
        var cancellationToken = _testContext.CancellationToken;
        WriteHalfCovered($"TestResults/run/{reportName}");
        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", "40");
        Assert.AreEqual(0, run.ExitCode, message: run.Output);
        Assert.Contains("Coverage gate passed: PASS: line 50.0% (min 40%)", run.Output);
        Assert.Contains("src/A.cs", run.Output); // the table was rendered
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Fail_Exits1_WithTheThresholdMessage_AndTheOffenders()
    {
        var cancellationToken = _testContext.CancellationToken;
        WriteHalfCovered();
        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", "80");
        // dotcov's own exit code, so a caller can tell FAIL from NODATA.
        Assert.AreEqual(1, run.ExitCode, message: run.Output);
        Assert.Contains("Coverage below threshold: FAIL: line 50.0% (min 80%)", run.Output);
        Assert.Contains("files below line threshold:", run.Output);
        Assert.Contains("  src/A.cs  50.0% lines", run.Output);
        Assert.Contains("    uncovered 2", run.Output);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task NoData_ParsedReportWithoutMeasurements_Exits3()
    {
        var cancellationToken = _testContext.CancellationToken;
        _ws.Write("TestResults/coverage.cobertura.xml", Cobertura.NewDoc());
        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", "80");
        Assert.AreEqual(3, run.ExitCode, message: run.Output);
        Assert.Contains("Coverage could not be measured: NODATA:", run.Output);
        Assert.DoesNotContain("No files matching", run.Output);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Disabled_EveryThresholdZero_Exits4()
    {
        var cancellationToken = _testContext.CancellationToken;
        WriteHalfCovered();
        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", "0", "--coverage-min-branch", "0");
        Assert.AreEqual(4, run.ExitCode, message: run.Output);
        Assert.Contains("Coverage gate is disabled", run.Output);
        Assert.Contains("DISABLED:", run.Output);
    }

    // ── Inputs ────────────────────────────────────────────────────────────────
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task MissingSearchDirectory_Exits5_NamingTheDirectory()
    {
        var cancellationToken = _testContext.CancellationToken;
        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", "40");
        Assert.AreEqual(5, run.ExitCode, message: run.Output);
        Assert.Contains($"Coverage search directory '{_ws.PathOf("TestResults")}' does not exist", run.Output);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task DirectoryWithoutMatches_Exits3_NamingThePattern()
    {
        var cancellationToken = _testContext.CancellationToken;
        _ws.Write("TestResults/notes.txt", "nothing here");
        var run = await RunBuild(cancellationToken, null, "--coverage-pattern", "**/coverage.xml", "--coverage-min-line", "40");
        // Nothing was measured: NODATA, as check reads a directory without matches.
        Assert.AreEqual(3, run.ExitCode, message: run.Output);
        Assert.Contains($"No files matching '**/coverage.xml' found in {_ws.PathOf("TestResults")}", run.Output);
        Assert.Contains("Coverage could not be measured: NODATA:", run.Output);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task DirectoryWithoutMatches_StillWritesTheNoDataJson()
    {
        var cancellationToken = _testContext.CancellationToken;
        _ws.Write("TestResults/notes.txt", "nothing here");
        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", "40", "--coverage-json-path", "out/coverage.json");
        Assert.AreEqual(3, run.ExitCode, message: run.Output);
        var gate = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(_ws.PathOf("out/coverage.json"), cancellationToken)).RootElement.GetProperty("gate");
        Assert.AreEqual("nodata", gate.GetProperty("outcome").GetString());
        Assert.AreEqual(3, gate.GetProperty("exitCode").GetInt32());
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Pattern_FindsNonCoverletNamesAndHiddenDirectories()
    {
        var cancellationToken = _testContext.CancellationToken;
        _ws.Write("TestResults/.hidden/coverage.xml", Cobertura.NewDoc().AddClass("src/A.cs", c => c.Line(1, hits: 1)));
        var run = await RunBuild(cancellationToken, null, "--coverage-pattern", "**/coverage.xml", "--coverage-min-line", "50");
        Assert.AreEqual(0, run.ExitCode, message: run.Output);
        Assert.Contains("PASS: line 100.0%", run.Output);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task MalformedReport_Exits5_NamingTheFile()
    {
        var cancellationToken = _testContext.CancellationToken;
        var bad = _ws.Write("TestResults/coverage.cobertura.xml", "<coverage><packa");
        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", "40");
        Assert.AreEqual(5, run.ExitCode, message: run.Output);
        Assert.Contains($"Could not parse coverage report {bad} (line 1, position ", run.Output);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task SeveralReports_AreMerged_AndNamed()
    {
        var cancellationToken = _testContext.CancellationToken;
        _ws.Write("TestResults/a/coverage.cobertura.xml", Cobertura.NewDoc().AddClass("src/A.cs", c => c.Line(1, 1)));
        _ws.Write("TestResults/b/coverage.cobertura.xml", Cobertura.NewDoc().AddClass("src/B.cs", c => c.Line(1, 0)));
        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", "40");
        Assert.AreEqual(0, run.ExitCode, message: run.Output);
        Assert.Contains("Merging 2 coverage reports", run.Output);
        Assert.Contains("PASS: line 50.0%", run.Output);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task UnreadableReport_Exits5_NamingTheCause()
    {
        var cancellationToken = _testContext.CancellationToken;
        var path = _ws.Write("TestResults/coverage.cobertura.xml", Cobertura.NewDoc().AddClass("src/A.cs", c => c.Line(1, 1)));
        // An exclusive handle refuses the build's open on every OS (.NET locks files on Unix too).
        await using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", "40");
        Assert.AreEqual(5, run.ExitCode, message: run.Output);
        Assert.Contains($"Could not read coverage reports in {Path.GetDirectoryName(path)}: ", run.Output);
    }

    // ── Parameters ────────────────────────────────────────────────────────────
    [TestMethod]
    [DataRow("--coverage-min-line", "eighty", "Invalid Coverage MinLine: 'eighty'")]
    [DataRow("--coverage-min-line", "80,5", "Invalid Coverage MinLine: '80,5'")]
    [DataRow("--coverage-exclude-generated-param", "yes", "Invalid Coverage ExcludeGeneratedParam: 'yes'")]
    [DataRow("--coverage-format", "xml", "Invalid Coverage Format: 'xml'")]
    [DataRow("--coverage-pattern", "cov/*.xml", "Invalid Coverage Pattern: 'cov/*.xml'")]
    // A leading '-' is consumed by Fallout's own argument grammar before the value reaches the
    // component, so the digits-only rejection of "-1" is pinned in CoverageParametersTests.
    [DataRow("--coverage-max-chars-param", "ten", "Invalid Coverage MaxCharsParam: 'ten'")]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task InvalidParameter_FailsBeforeReadingAnyReport(string flag, string value, string expected)
    {
        var cancellationToken = _testContext.CancellationToken;
        WriteHalfCovered();
        // A threshold in the dimension the case does not test: the named parameter is the only error.
        var run = await RunBuild(cancellationToken, null, flag, value, "--coverage-min-branch", "0");
        Assert.AreEqual(2, run.ExitCode, message: run.Output);
        Assert.Contains(expected, run.Output);
        Assert.DoesNotContain("src/A.cs", run.Output);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task MaxChars_BelowDocumentSize_FailsNamingTheFile()
    {
        var cancellationToken = _testContext.CancellationToken;
        var path = WriteHalfCovered();
        var run = await RunBuild(cancellationToken, null, "--coverage-max-chars-param", "50", "--coverage-min-line", "40");
        Assert.AreEqual(5, run.ExitCode, message: run.Output);
        Assert.Contains($"Could not parse coverage report {path}", run.Output);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Format_MarkdownAlias_RendersMarkdownWithTheVerdict()
    {
        var cancellationToken = _testContext.CancellationToken;
        WriteHalfCovered();
        var run = await RunBuild(cancellationToken, null, "--coverage-format", "md", "--coverage-min-line", "40");
        Assert.AreEqual(0, run.ExitCode, message: run.Output);
        Assert.Contains("**Line coverage:** 50.0% (1/2)", run.Output);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ExcludeGenerated_TrueDropsGeneratedFiles_FalseKeepsThem()
    {
        var cancellationToken = _testContext.CancellationToken;
        _ws.Write("TestResults/coverage.cobertura.xml", Cobertura.NewDoc().AddClass("src/A.cs", c => c.Line(1, hits: 1)).AddClass("src/Migrations/M1.cs", c => c.Line(1, hits: 0).Line(2, hits: 0)));
        var kept = await RunBuild(cancellationToken, null, "--coverage-min-line", "30");
        var excluded = await RunBuild(cancellationToken, null, "--coverage-min-line", "30", "--coverage-exclude-generated-param", "true");
        Assert.AreEqual(0, kept.ExitCode, message: kept.Output);
        Assert.Contains("Migrations/M1.cs", kept.Output);
        Assert.DoesNotContain("Excluded ", kept.Output);
        // Gone from the table; named once, in the exclusion line.
        Assert.Contains("Excluded 1 files by", excluded.Output);
        Assert.AreEqual(1, excluded.Output.Split("Migrations/M1.cs").Length - 1, message: excluded.Output);
        Assert.Contains("PASS: line 100.0%", excluded.Output);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ExcludeTestCode_TrueDropsTestCode_AndLogsWhatItExcluded()
    {
        var cancellationToken = _testContext.CancellationToken;
        _ws.Write("TestResults/coverage.cobertura.xml", Cobertura.NewDoc().AddClass("src/A.cs", c => c.Line(1, hits: 1).Line(2, hits: 0)).AddClass("tests/A.Tests/ATests.cs", c => c.Line(1, hits: 1).Line(2, hits: 1)));
        var kept = await RunBuild(cancellationToken, null, "--coverage-min-line", "70");
        var excluded = await RunBuild(cancellationToken, null, "--coverage-min-line", "70", "--coverage-exclude-test-code-param", "true");
        Assert.AreEqual(0, kept.ExitCode, message: kept.Output);
        Assert.AreEqual(1, excluded.ExitCode, message: excluded.Output);
        Assert.Contains("Coverage below threshold: FAIL: line 50.0% (min 70%)", excluded.Output);
        Assert.Contains("Excluded 1 files by", excluded.Output);
        Assert.Contains("tests/A.Tests/ATests.cs", excluded.Output);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ExcludeExternal_TrueDropsDependencyFiles_TheWarningNamesThemOtherwise()
    {
        var cancellationToken = _testContext.CancellationToken;
        _ws.Write("TestResults/coverage.cobertura.xml", Cobertura.NewDoc().AddClass("/repo/src/A.cs", c => c.Line(1, hits: 1)).AddClass("/_/src/Lib/Client.cs", c => c.Line(1, hits: 0)));
        var kept = await RunBuild(cancellationToken, null, "--coverage-min-line", "80");
        var excluded = await RunBuild(cancellationToken, null, "--coverage-min-line", "80", "--coverage-exclude-external-param", "true");
        Assert.AreEqual(1, kept.ExitCode, message: kept.Output);
        Assert.Contains("ExternalSources", kept.Output);
        Assert.AreEqual(0, excluded.ExitCode, message: excluded.Output);
        Assert.Contains("Excluded 1 files by", excluded.Output);
        Assert.DoesNotContain("ExternalSources", excluded.Output);
    }

    // ── Thresholds: explicit, 0 to 100, one disables the other ────────────────
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task NoThreshold_FailsNamingTheParameters_BeforeReadingAnyReport()
    {
        var cancellationToken = _testContext.CancellationToken;
        // A malformed report would fail the parse; the missing threshold fails first.
        _ws.Write("TestResults/coverage.cobertura.xml", "<coverage><packa");
        var run = await RunBuild(cancellationToken);
        Assert.AreEqual(2, run.ExitCode, message: run.Output);
        Assert.Contains("Missing Coverage MinLine and Coverage MinBranch: the coverage gate has no default threshold.", run.Output);
        Assert.DoesNotContain("Could not parse", run.Output);
    }

    [TestMethod]
    [DataRow("--coverage-min-line", "150", "Invalid Coverage MinLine: '150' (expected a number from 0 to 100).")]
    [DataRow("--coverage-min-branch", "Infinity", "Invalid Coverage MinBranch: 'Infinity' (expected a number from 0 to 100).")]
    [DataRow("--coverage-min-line", "NaN", "Invalid Coverage MinLine: 'NaN' (expected a number from 0 to 100).")]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ThresholdOutsideZeroToHundred_FailsNamingTheParameter(string flag, string value, string expected)
    {
        var cancellationToken = _testContext.CancellationToken;
        WriteHalfCovered();
        var run = await RunBuild(cancellationToken, null, flag, value);
        Assert.AreEqual(2, run.ExitCode, message: run.Output);
        Assert.Contains(expected, run.Output);
        Assert.DoesNotContain("src/A.cs", run.Output);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task BranchThresholdAlone_LeavesTheLineDimensionUnchecked()
    {
        var cancellationToken = _testContext.CancellationToken;
        _ws.Write("TestResults/coverage.cobertura.xml", Cobertura.NewDoc().AddClass("src/M.cs", c => c.Line(1, hits: 0).Branch(2, "100% (2/2)")));
        var run = await RunBuild(cancellationToken, null, "--coverage-min-branch", "90");
        Assert.AreEqual(0, run.ExitCode, message: run.Output);
        Assert.Contains("Coverage gate passed: PASS: line 50.0% (min 0%), branch 100.0% (min 90%)", run.Output);
    }

    // ── File identity: lexical by default, probing and an explicit root opt in ──
    private void WriteTwoRootReport() => _ws.Write("TestResults/coverage.cobertura.xml", Cobertura.NewDoc().WithSource("src").WithSource("tests").AddClass("test_app.py", c => c.Line(1, hits: 1).Line(2, hits: 0)));
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AmbiguousIdentity_IsNoData_ByDefault()
    {
        var cancellationToken = _testContext.CancellationToken;
        WriteTwoRootReport();
        _ws.Write("tests/test_app.py", ""); // on disk, but the default never looks
        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", "40");
        Assert.AreEqual(3, run.ExitCode, message: run.Output);
        Assert.Contains("Coverage could not be measured: NODATA: line 50.0% (min 40%), branch n/a (min 0%) - 1 file has ambiguous identity across 2 source roots", run.Output);
        Assert.Contains("relative filename 'test_app.py' could belong to any of 2 <source> roots", run.Output);
    }

    [TestMethod]
    [DataRow("--coverage-probe-sources-param", "true")]
    [DataRow("--coverage-source-root", "tests")]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task AmbiguousIdentity_ResolvedByEitherParameter_IsAMeasuredPass(string flag, string value)
    {
        var cancellationToken = _testContext.CancellationToken;
        WriteTwoRootReport();
        _ws.Write("tests/test_app.py", "");
        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", "40", flag, value);
        Assert.AreEqual(0, run.ExitCode, message: run.Output);
        Assert.Contains("Coverage gate passed: PASS: line 50.0% (min 40%)", run.Output);
        Assert.Contains("tests/test_app.py", run.Output);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task SourceRoot_NotDeclared_FailsNamingTheParameter()
    {
        var cancellationToken = _testContext.CancellationToken;
        WriteTwoRootReport();
        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", "40", "--coverage-source-root", "lib");
        Assert.AreEqual(2, run.ExitCode, message: run.Output);
        Assert.Contains("Invalid Coverage SourceRoot: source root 'lib' is not declared by ", run.Output);
        Assert.DoesNotContain("PASS:", run.Output);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ProbeSourcesAndSourceRoot_Together_FailBeforeReadingAnyReport()
    {
        var cancellationToken = _testContext.CancellationToken;
        WriteTwoRootReport();
        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", "40", "--coverage-probe-sources-param", "true", "--coverage-source-root", "tests");
        Assert.AreEqual(2, run.ExitCode, message: run.Output);
        Assert.Contains("Coverage ProbeSourcesParam and Coverage SourceRoot are alternatives", run.Output);
        Assert.DoesNotContain("test_app.py", run.Output);
    }

    // ── Diagnostics and summary ───────────────────────────────────────────────
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ParserWarnings_AreLoggedCompletely_AndDoNotChangeTheVerdict()
    {
        var cancellationToken = _testContext.CancellationToken;
        _ws.Write("TestResults/coverage.cobertura.xml", Cobertura.NewDoc().AddClass("src/A.cs", c => c.MalformedLine("1", "lots").MalformedLine("2", "many").Line(3, hits: 1)));
        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", "30");
        Assert.AreEqual(0, run.ExitCode, message: run.Output);
        Assert.Contains("src/A.cs:1: hits='lots' could not be parsed", run.Output);
        Assert.Contains("src/A.cs:2: hits='many' could not be parsed", run.Output);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task StepSummary_IsWrittenOnPassAndFail_FromTheSameGate()
    {
        var cancellationToken = _testContext.CancellationToken;
        WriteHalfCovered();
        var summary = _ws.PathOf("summary.md");
        var env = new Dictionary<string, string?>
        {
            ["GITHUB_STEP_SUMMARY"] = summary
        };
        var pass = await RunBuild(cancellationToken, env, "--coverage-min-line", "40");
        var passMarkdown = await File.ReadAllTextAsync(summary, cancellationToken);
        File.Delete(summary);
        var fail = await RunBuild(cancellationToken, env, "--coverage-min-line", "80");
        var failMarkdown = await File.ReadAllTextAsync(summary, cancellationToken);
        Assert.AreEqual(0, pass.ExitCode, message: pass.Output);
        Assert.Contains("PASS: line 50.0% (min 40%)", passMarkdown);
        Assert.AreEqual(1, fail.ExitCode, message: fail.Output);
        Assert.Contains("- `src/A.cs` — 50.0% lines — uncovered 2", failMarkdown);
        Assert.Contains("FAIL: line 50.0% (min 80%)", failMarkdown);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task StepSummary_UnwritableTarget_WarnsAndKeepsThePass()
    {
        var cancellationToken = _testContext.CancellationToken;
        WriteHalfCovered();
        var env = new Dictionary<string, string?>
        {
            ["GITHUB_STEP_SUMMARY"] = _ws.PathOf("no-such-dir/summary.md")
        };
        var run = await RunBuild(cancellationToken, env, "--coverage-min-line", "40");
        Assert.AreEqual(0, run.ExitCode, message: run.Output);
        Assert.Contains("Could not write GitHub step summary", run.Output);
        Assert.Contains("Coverage gate passed", run.Output);
    }

    // ── Badge ─────────────────────────────────────────────────────────────────
    [TestMethod]
    [DataRow("40", false, 0, "50.0%", "brightgreen")]
    [DataRow("80", false, 1, "50.0%", "red")]
    [DataRow("0", false, 4, "disabled", "yellow")]
    [DataRow("80", true, 3, "no data", "lightgrey")]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task BadgeDirectory_WritesTheGateBadge_ForEveryOutcome(string minLine, bool empty, int exitCode, string message, string color)
    {
        var cancellationToken = _testContext.CancellationToken;
        if (empty)
            _ws.Write("TestResults/coverage.cobertura.xml", Cobertura.NewDoc());
        else
            WriteHalfCovered();
        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", minLine, "--coverage-badge-directory", "out/badge");
        Assert.AreEqual(exitCode, run.ExitCode, message: run.Output);
        await AssertBadge(_ws.PathOf("out/badge"), message, color);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task BadgeDirectory_Absolute_IsUsedAsGiven()
    {
        var cancellationToken = _testContext.CancellationToken;
        WriteHalfCovered();
        var directory = _ws.PathOf("elsewhere/badge");
        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", "40", "--coverage-badge-directory", directory);
        Assert.AreEqual(0, run.ExitCode, message: run.Output);
        await AssertBadge(directory, "50.0%", "brightgreen");
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task BadgeDirectory_Unset_WritesNoBadge()
    {
        var cancellationToken = _testContext.CancellationToken;
        WriteHalfCovered();
        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", "40");
        Assert.AreEqual(0, run.ExitCode, message: run.Output);
        Assert.IsEmpty(Directory.EnumerateFiles(_ws.Root, "coverage-badge.*", SearchOption.AllDirectories));
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task BadgeDirectory_Unwritable_FailsAPassingGate()
    {
        var cancellationToken = _testContext.CancellationToken;
        WriteHalfCovered();
        _ws.Write("blocked", "a file where the directory should go");
        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", "40", "--coverage-badge-directory", "blocked");
        Assert.AreEqual(5, run.ExitCode, message: run.Output);
        Assert.Contains("Coverage gate passed, but its badge could not be written:", run.Output);
        Assert.DoesNotContain("Coverage gate passed: PASS", run.Output);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task BadgeDirectory_Unwritable_KeepsTheFailingVerdict()
    {
        var cancellationToken = _testContext.CancellationToken;
        WriteHalfCovered();
        _ws.Write("blocked", "a file where the directory should go");
        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", "80", "--coverage-badge-directory", "blocked");
        Assert.AreEqual(1, run.ExitCode, message: run.Output);
        Assert.Contains("Could not write coverage badge to blocked:", run.Output);
        Assert.Contains("Coverage below threshold: FAIL: line 50.0% (min 80%)", run.Output);
    }

    private static async Task AssertBadge(string directory, string message, string color)
    {
        var svg = await File.ReadAllTextAsync(Path.Combine(directory, "coverage-badge.svg"));
        var json = await File.ReadAllTextAsync(Path.Combine(directory, "coverage-badge.json"));
        Assert.Contains($">{message}</text>", svg);
        Assert.AreEqual($$"""{"schemaVersion":1,"label":"Coverage","message":"{{message}}","color":"{{color}}"}""", json);
    }

    // ── JSON: the check document, on the log and in a file ────────────────────
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Format_Json_LogsTheGateDocument()
    {
        var cancellationToken = _testContext.CancellationToken;
        WriteHalfCovered();
        var run = await RunBuild(cancellationToken, null, "--coverage-format", "json", "--coverage-min-line", "80");
        Assert.AreEqual(1, run.ExitCode, message: run.Output);
        Assert.Contains("\"command\": \"check\"", run.Output);
        Assert.Contains("\"outcome\": \"fail\"", run.Output);
        Assert.Contains("\"uncovered\": \"2\"", run.Output);
    }

    [TestMethod]
    [DataRow("40", 0, "pass")]
    [DataRow("80", 1, "fail")]
    [DataRow("0", 4, "disabled")]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task JsonPath_WritesTheGateDocument_ForEveryOutcome(string minLine, int exitCode, string outcome)
    {
        var cancellationToken = _testContext.CancellationToken;
        var report = WriteHalfCovered();
        // Relative to the root, like the badge directory; the table still goes to the log.
        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", minLine, "--coverage-json-path", "out/coverage.json");
        Assert.AreEqual(exitCode, run.ExitCode, message: run.Output);
        Assert.Contains("TOTAL", run.Output);
        var root = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(_ws.PathOf("out/coverage.json"), cancellationToken)).RootElement;
        Assert.AreEqual(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual("check", root.GetProperty("command").GetString());
        Assert.AreSequenceEqual([report], root.GetProperty("inputs").EnumerateArray().Select(static i => i.GetString()!), SequenceOrder.InOrder);
        Assert.AreEqual(outcome, root.GetProperty("gate").GetProperty("outcome").GetString());
        Assert.AreEqual(exitCode, root.GetProperty("gate").GetProperty("exitCode").GetInt32());
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task JsonPath_Unwritable_FailsAPassingGate_With5()
    {
        var cancellationToken = _testContext.CancellationToken;
        WriteHalfCovered();
        _ws.Write("blocked", "a file where the directory should go");
        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", "40", "--coverage-json-path", "blocked/coverage.json");
        Assert.AreEqual(5, run.ExitCode, message: run.Output);
        Assert.Contains("Coverage gate passed, but its JSON report could not be written:", run.Output);
        Assert.DoesNotContain("Coverage gate passed: PASS", run.Output);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task JsonPath_Unwritable_KeepsTheFailingVerdict()
    {
        var cancellationToken = _testContext.CancellationToken;
        WriteHalfCovered();
        _ws.Write("blocked", "a file where the directory should go");
        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", "80", "--coverage-json-path", "blocked/coverage.json");
        Assert.AreEqual(1, run.ExitCode, message: run.Output);
        Assert.Contains("Could not write coverage JSON to blocked/coverage.json:", run.Output);
        Assert.Contains("Coverage below threshold: FAIL: line 50.0% (min 80%)", run.Output);
    }

    // ── Target graph ──────────────────────────────────────────────────────────
    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task WithoutICompile_ReportCoverageRunsAlone()
    {
        var cancellationToken = _testContext.CancellationToken;
        WriteHalfCovered();
        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", "40");
        Assert.AreEqual(0, run.ExitCode, message: run.Output);
        Assert.DoesNotContain("compile-target-ran", run.Output);
        Assert.DoesNotContain("Compile", run.Output);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task WithICompile_CompileRunsBeforeReportCoverage()
    {
        var cancellationToken = _testContext.CancellationToken;
        // ICompile requires a solution (IHasSolution.Solution is [Required]); Fallout reads it
        // from .fallout/parameters.json. A header-only .sln is enough for the loader.
        WriteHalfCovered();
        _ws.Write(".fallout/parameters.json", "{ \"Solution\": \"Test.sln\" }\n");
        _ws.Write("Test.sln", "\nMicrosoft Visual Studio Solution File, Format Version 12.00\n# Visual Studio Version 17\nVisualStudioVersion = 17.0.31903.59\nMinimumVisualStudioVersion = 10.0.40219.1\nGlobal\nEndGlobal\n");
        var env = new Dictionary<string, string?>
        {
            ["DOTCOV_TESTBUILD"] = "compile"
        };
        var run = await RunBuild(cancellationToken, env, "--coverage-min-line", "40");
        Assert.AreEqual(0, run.ExitCode, message: run.Output);
        var compileAt = run.Output.IndexOf("compile-target-ran", StringComparison.Ordinal);
        var coverageAt = run.Output.IndexOf("Coverage gate passed", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, compileAt, message: run.Output);
        Assert.IsGreaterThan(compileAt, coverageAt);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Cancellation_TerminatesBuildBeforeReturning()
    {
        var cancellationToken = _testContext.CancellationToken;
        var pipeName = Guid.NewGuid().ToString("N");
        await using var ready = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
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
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await run.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
            Assert.IsTrue(child.HasExited);
        }
        finally
        {
            // Reap the child even when the runner failed to kill it.
            if (!child.HasExited)
                child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(CancellationToken.None);
        }
    }
}
