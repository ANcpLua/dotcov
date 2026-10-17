using System.Text.RegularExpressions;
using DotCov.Tests.Infrastructure;
using DotCov.Tool;

namespace DotCov.Tests;
/// <summary>
/// The stream, exit-code, and colour contract of the real dotcov process: `dotcov test` with a
/// stand-in dotnet (<c>DOTNET_HOST_PATH</c>), and colour, which only a process whose stdout is
/// the console can switch on. The child's environment is copied from this process.
/// </summary>
[TestClass]
[ResourceLock(ProcessState.Environment)]
[TestCategory("Integration")]
public sealed partial class CliProcessContractTests(TestContext testContext) : IDisposable
{
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-process-contract-");
    public void Dispose() => _ws.Dispose();
    [GeneratedRegex(@"^reports: TestResults[/\\][0-9a-f]{32}$")]
    private static partial Regex ReportsLine();
    /// <summary>A CI log viewer that renders ANSI, with every override of the cascade cleared.</summary>
    private static Dictionary<string, string?> Ci(params (string Name, string? Value)[] extra)
    {
        var environment = new Dictionary<string, string?>
        {
            ["GITHUB_ACTIONS"] = "true",
            ["NO_COLOR"] = null,
            ["FORCE_COLOR"] = null,
            ["CLICOLOR_FORCE"] = null,
            ["CLICOLOR"] = null,
            ["TERM"] = null
        };
        foreach (var(name, value)in extra)
            environment[name] = value;
        return environment;
    }

    private string HalfCovered(string relative = "fixture/half.cobertura.xml") => _ws.Write(relative, Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("M", "()", "2", m => m.Line(1, hits: 1).Line(2, hits: 0))));
    /// <summary>A stand-in dotnet: <paramref name = "unix"/> as a sh script, <paramref name = "windows"/> as a batch file.</summary>
    private string Script(string name, string unix, string windows)
    {
        if (OperatingSystem.IsWindows())
            return _ws.Write($"{name}.cmd", windows.ReplaceLineEndings("\r\n"));
        var path = _ws.Write(name, unix.ReplaceLineEndings("\n"));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private Task<ChildRun> Dotcov(IReadOnlyDictionary<string, string?> environment, CancellationToken cancellationToken, params string[] args) => DotcovProcess.RunAsync(_ws.Root, environment, cancellationToken, args);
    private Task<ChildRun> DotcovTest(string dotnet, CancellationToken cancellationToken, params string[] args) => Dotcov(new Dictionary<string, string?> { ["DOTNET_HOST_PATH"] = dotnet }, cancellationToken, ["test", ..args]);
    // ── dotcov test: dotnet test output and the table on stdout, the verdict leading stderr ──
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Test_StdoutIsTheRunAndTheTable_StderrIsTheVerdictThenTheReports()
    {
        var cancellationToken = testContext.CancellationToken;
        var dotnet = FakeDotnet.Write(_ws, HalfCovered());
        var run = await DotcovTest(dotnet, cancellationToken, "--min-line", "40");
        Assert.AreEqual(0, run.ExitCode, message: run.Output);
        var stdout = run.StdOut.Split(Environment.NewLine);
        Assert.StartsWith("> ", stdout[0]);
        Assert.Contains("src/A.cs", run.StdOut);
        Assert.DoesNotContain("PASS:", run.StdOut);
        var stderr = run.StdErr.Split(Environment.NewLine);
        Assert.StartsWith("PASS: line 50.0% (min 40%)", stderr[0]);
        Assert.MatchesRegex(ReportsLine(), stderr[1]);
        // The path is relative to the working directory and holds the run's report.
        Assert.IsTrue(File.Exists(_ws.PathOf(Path.Combine(stderr[1]["reports: ".Length..], "coverage.cobertura.xml"))));
        Assert.HasCount(3, stderr);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Test_FailedRun_Exits6_AndIsNoVerdict()
    {
        var cancellationToken = testContext.CancellationToken;
        var dotnet = Script("failing-dotnet", """
            #!/bin/sh
            echo "Failed: 1, Passed: 0"
            exit 3
            """, """
            @echo off
            echo Failed: 1, Passed: 0
            exit /b 3
            """);
        var run = await DotcovTest(dotnet, cancellationToken, "--min-line", "40");
        // 6, apart from 5 for a report dotcov could not read.
        Assert.AreEqual(6, run.ExitCode);
        Assert.Contains("Failed: 1, Passed: 0", run.StdOut);
        Assert.AreEqual($"error: dotnet test exited with code 3; coverage was not evaluated.{Environment.NewLine}", run.StdErr);
    }

    // JSON and markdown hold the dotnet test log back, so stdout stays a document; a failed run
    // shows it after the error line that is its outcome.
    [TestMethod]
    [DataRow("json")]
    [DataRow("md")]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Test_FailedRun_InADocumentFormat_ErrorFirst_ThenTheHeldBackLog(string format)
    {
        var cancellationToken = testContext.CancellationToken;
        var dotnet = Script("failing-dotnet", """
            #!/bin/sh
            echo "Failed: 1, Passed: 0"
            exit 3
            """, """
            @echo off
            echo Failed: 1, Passed: 0
            exit /b 3
            """);
        var run = await DotcovTest(dotnet, cancellationToken, "--min-line", "40", "--format", format);
        Assert.AreEqual(6, run.ExitCode);
        Assert.IsEmpty(run.StdOut);
        Assert.StartsWith($"error: dotnet test exited with code 3; coverage was not evaluated.{Environment.NewLine}> ", run.StdErr);
        Assert.Contains("Failed: 1, Passed: 0", run.StdErr);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Test_RunWithoutAReport_IsNoData_WithoutPatternAdvice()
    {
        var cancellationToken = testContext.CancellationToken;
        // test takes no --pattern, so the empty-directory advice would name a flag it rejects.
        var dotnet = Script("silent-dotnet", "#!/bin/sh\nexit 0\n", "@exit /b 0\n");
        var run = await DotcovTest(dotnet, cancellationToken, "--min-line", "40");
        Assert.AreEqual(DotCovCli.NoDataExitCode, run.ExitCode);
        var stderr = run.StdErr.Split(Environment.NewLine);
        Assert.StartsWith("NODATA: ", stderr[0]);
        Assert.MatchesRegex(ReportsLine(), stderr[1]);
        Assert.DoesNotContain("warning: no file", run.StdErr);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Test_HelpAfterTheSeparator_GoesToDotnetTest()
    {
        var cancellationToken = testContext.CancellationToken;
        var dotnet = FakeDotnet.Write(_ws, HalfCovered());
        var run = await DotcovTest(dotnet, cancellationToken, "--min-line", "40", "--", "--help");
        Assert.AreEqual(0, run.ExitCode, message: run.Output);
        Assert.EndsWith(" --help", run.StdOut.Split(Environment.NewLine)[0]);
        Assert.DoesNotContain("Options:", run.StdOut);
    }

    // ── Colour: on in a CI log, off with --no-color or NO_COLOR ──
    [TestMethod]
    [DataRow("report")]
    [DataRow("crap")]
    [DataRow("diff")]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task CiTable_IsColoured_UnlessNoColorOrNO_COLOR(string command)
    {
        var cancellationToken = testContext.CancellationToken;
        var report = HalfCovered();
        string[] args = command is "diff" ? [command, report, report] : [command, report];
        var coloured = await Dotcov(Ci(), cancellationToken, args);
        var flag = await Dotcov(Ci(), cancellationToken, [..args, "--no-color"]);
        var variable = await Dotcov(Ci(("NO_COLOR", "1")), cancellationToken, args);
        Assert.IsTrue(AnsiStrip.ContainsAnsi(coloured.StdOut), message: coloured.StdOut);
        foreach (var plain in new[]
        {
            flag,
            variable
        }

        )
        {
            Assert.AreEqual(coloured.ExitCode, plain.ExitCode);
            Assert.IsFalse(AnsiStrip.ContainsAnsi(plain.Output), message: plain.Output);
            Assert.AreEqual(AnsiStrip.From(coloured.StdOut), plain.StdOut);
        }
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Test_NoColor_ReachesDotnetTest_AsNO_COLOR()
    {
        var cancellationToken = testContext.CancellationToken;
        var fake = FakeDotnet.Write(_ws, HalfCovered());
        var dotnet = Script("colour-dotnet", $"""
            #!/bin/sh
            if [ -z "$NO_COLOR" ]; then printf '\033[32mdotnet colour\033[0m\n'; fi
            exec "{fake}" "$@"
            """, $"""
            @echo off
            if not defined NO_COLOR echo {'\e'}[32mdotnet colour{'\e'}[0m
            call "{fake}" %*
            exit /b %errorlevel%
            """);
        var coloured = await Dotcov(Ci(("DOTNET_HOST_PATH", dotnet)), cancellationToken, "test", "--min-line", "40");
        var plain = await Dotcov(Ci(("DOTNET_HOST_PATH", dotnet)), cancellationToken, "test", "--min-line", "40", "--no-color");
        Assert.AreEqual(0, coloured.ExitCode, message: coloured.Output);
        Assert.Contains("\e[32mdotnet colour", coloured.StdOut);
        Assert.AreEqual(0, plain.ExitCode, message: plain.Output);
        Assert.Contains("src/A.cs", plain.StdOut);
        Assert.IsFalse(AnsiStrip.ContainsAnsi(plain.Output), message: plain.Output);
    }
}
