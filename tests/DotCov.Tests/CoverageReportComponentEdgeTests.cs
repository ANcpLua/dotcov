using System.Diagnostics;
using System.Globalization;
using DotCov.Fallout;
using DotCov.Tests.Infrastructure;
using DotCov.Tool;

namespace DotCov.Tests;
/// <summary>
/// Edge cases of <see cref = "ICoverageReport"/> observed through the minimal consuming build in
/// <c>tests/DotCov.Fallout.TestBuild</c>, run as a real Fallout process like
/// <see cref = "FalloutBuildTests"/>: the threshold grammar the component shares with the CLI and
/// the step-summary variable's "set" test. Every case drives the component through its public
/// target; the parameter parser is never called directly.
/// </summary>
[TestClass]
[ResourceLock(ProcessState.Environment)]
[TestCategory("Integration")]
public sealed class CoverageReportComponentEdgeTests : IDisposable
{
    private readonly TestContext _testContext;
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-fallout-edge-");
    public CoverageReportComponentEdgeTests(TestContext testContext)
    {
        _testContext = testContext;
        _ws.CreateDirectory(".fallout");
    }

    public void Dispose() => _ws.Dispose();
    private async Task<ChildRun> RunBuild(CancellationToken cancellationToken, IReadOnlyDictionary<string, string?>? environment, params string[] args)
    {
        var testsDir = AppContext.BaseDirectory;
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
            Path.Combine(testsDir, "DotCov.Tests.deps.json"),
            "--runtimeconfig",
            Path.Combine(testsDir, "DotCov.Tests.runtimeconfig.json"),
            Path.Combine(testsDir, "DotCov.Fallout.TestBuild.dll"),
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
        return run with
        {
            StdOut = AnsiStrip.From(run.StdOut),
            StdErr = AnsiStrip.From(run.StdErr)
        };
    }

    private void WriteHalfCovered() => _ws.Write("TestResults/run/coverage.cobertura.xml", Cobertura.NewDoc().AddClass("src/A.cs", c => c.Line(1, hits: 1).Line(2, hits: 0)));
    [TestMethod]
    [DataRow("--coverage-min-line", "NaN", "Invalid Coverage MinLine: 'NaN'")]
    [DataRow("--coverage-min-line", "nan", "Invalid Coverage MinLine: 'nan'")]
    [DataRow("--coverage-min-branch", "NaN", "Invalid Coverage MinBranch: 'NaN'")]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task NaNThreshold_FailsNamingTheParameter_BeforeReadingAnyReport(string flag, string value, string expected)
    {
        var cancellationToken = _testContext.CancellationToken;
        // The CLI rejects NaN (every comparison with it is false). The component promises the
        // CLI's grammar, so it fails on the parameter instead of reading the reports and issuing
        // a verdict about a threshold that does not exist.
        WriteHalfCovered();
        var run = await RunBuild(cancellationToken, null, "--coverage-min-line", "40", flag, value);
        Assert.AreNotEqual(0, run.ExitCode);
        Assert.Contains(expected, run.Output);
        Assert.DoesNotContain("src/A.cs", run.Output);
        Assert.DoesNotContain("Coverage below threshold", run.Output);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task NegativeNaNThreshold_FromTheEnvironment_FailsNamingTheParameter()
    {
        var cancellationToken = _testContext.CancellationToken;
        // Fallout does not bind a command-line value that starts with '-', so "-NaN" reaches the
        // component through its environment-variable binding, the other public route.
        WriteHalfCovered();
        var env = new Dictionary<string, string?>
        {
            ["COVERAGE_MIN_BRANCH"] = "-NaN"
        };
        var run = await RunBuild(cancellationToken, env, "--coverage-min-line", "40");
        Assert.AreNotEqual(0, run.ExitCode);
        Assert.Contains("Invalid Coverage MinBranch: '-NaN'", run.Output);
        Assert.DoesNotContain("Coverage below threshold", run.Output);
    }

    /// <summary>
    /// The component promises the CLI's threshold grammar. Each spelling goes through both public
    /// entry points — <c>dotcov check --min-line</c> and the real <c>ReportCoverage</c> target —
    /// and the two must agree on whether it is a valid threshold.
    /// </summary>
    [TestMethod]
    [DataRow("80")]
    [DataRow("1e2")]
    [DataRow(" 80 ")]
    [DataRow("-0")]
    [DataRow("Infinity")]
    [DataRow("NaN")]
    [DataRow("80%")]
    [DataRow(",5")]
    [DataRow("80,5")]
    [DataRow("0x50")]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Threshold_ComponentAcceptsExactlyWhatTheCliAccepts(string value)
    {
        var cancellationToken = _testContext.CancellationToken;
        WriteHalfCovered();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        await DotCovCli.RunAsync(["check", _ws.PathOf("TestResults"), "--min-line", value], stdout, stderr);
        var cliRejects = stderr.ToString().StartsWith("error: Invalid --min-line value:", StringComparison.Ordinal);
        // Bound through the environment, so values Fallout's command line would not bind as a
        // value ("-0", " 80 ") still reach the component's parser.
        var run = await RunBuild(cancellationToken, new Dictionary<string, string?> { ["COVERAGE_MIN_LINE"] = value });
        var componentRejects = run.Output.Contains("Invalid Coverage MinLine:", StringComparison.Ordinal);
        Assert.AreEqual(cliRejects, componentRejects, message: string.Create(CultureInfo.InvariantCulture, $"CLI stderr: {stderr}; component output: {run.Output}"));
        if (componentRejects)
            Assert.AreNotEqual(0, run.ExitCode);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task EmptyStepSummaryVariable_IsUnset_NotAnUnwritableTarget()
    {
        var cancellationToken = _testContext.CancellationToken;
        // README: the summary is appended "when that variable is set". An empty value names no
        // file, so it is neither written nor warned about.
        WriteHalfCovered();
        var env = new Dictionary<string, string?>
        {
            ["GITHUB_STEP_SUMMARY"] = ""
        };
        var run = await RunBuild(cancellationToken, env, "--coverage-min-line", "40");
        Assert.AreEqual(0, run.ExitCode, message: run.Output);
        Assert.Contains("Coverage gate passed: PASS: line 50.0% (min 40%)", run.Output);
        Assert.DoesNotContain("Could not write GitHub step summary", run.Output);
    }
}
