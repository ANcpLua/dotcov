using System.Text.RegularExpressions;
using DotCov.Tests.Infrastructure;
using DotCov.Tool;

namespace DotCov.Tests;

/// <summary>
/// The stream, exit-code, and colour contract of the real dotcov process: `dotcov test` with a
/// stand-in dotnet (<c>DOTNET_HOST_PATH</c>), and colour, which only a process whose stdout is
/// the console can switch on. The child's environment is copied from this process.
/// </summary>
[NotInParallel(ProcessState.Environment)]
[Category("Integration")]
[Timeout(60_000)]
public sealed partial class CliProcessContractTests : IDisposable
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
        foreach (var (name, value) in extra) environment[name] = value;
        return environment;
    }

    private string HalfCovered(string relative = "fixture/half.cobertura.xml") =>
        _ws.Write(relative, Cobertura.NewDoc()
            .AddClass("src/A.cs", "App.A", c => c.Method("M", "()", "2", m => m.Line(1, hits: 1).Line(2, hits: 0))));

    /// <summary>A stand-in dotnet: <paramref name="unix"/> as a sh script, <paramref name="windows"/> as a batch file.</summary>
    private string Script(string name, string unix, string windows)
    {
        if (OperatingSystem.IsWindows())
            return _ws.Write($"{name}.cmd", windows.ReplaceLineEndings("\r\n"));

        var path = _ws.Write(name, unix.ReplaceLineEndings("\n"));
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private Task<ChildRun> Dotcov(CancellationToken cancellationToken, IReadOnlyDictionary<string, string?> environment, params string[] args) =>
        DotcovProcess.RunAsync(cancellationToken, _ws.Root, environment, args);

    private Task<ChildRun> DotcovTest(CancellationToken cancellationToken, string dotnet, params string[] args) =>
        Dotcov(cancellationToken, new Dictionary<string, string?> { ["DOTNET_HOST_PATH"] = dotnet }, ["test", .. args]);

    // ── dotcov test: dotnet test output and the table on stdout, the verdict leading stderr ──

    [Test]
    public async Task Test_StdoutIsTheRunAndTheTable_StderrIsTheVerdictThenTheReports(CancellationToken cancellationToken)
    {
        var dotnet = FakeDotnet.Write(_ws, HalfCovered());

        var run = await DotcovTest(cancellationToken, dotnet, "--min-line", "40");

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.Output);
        var stdout = run.StdOut.Split(Environment.NewLine);
        await Assert.That(stdout[0]).StartsWith("> ");
        await Assert.That(run.StdOut).Contains("src/A.cs");
        await Assert.That(run.StdOut).DoesNotContain("PASS:");
        var stderr = run.StdErr.Split(Environment.NewLine);
        await Assert.That(stderr[0]).StartsWith("PASS: line 50.0% (min 40%)");
        await Assert.That(stderr[1]).Matches(ReportsLine());
        // The path is relative to the working directory and holds the run's report.
        await Assert.That(File.Exists(_ws.PathOf(Path.Combine(stderr[1]["reports: ".Length..], "coverage.cobertura.xml")))).IsTrue();
        await Assert.That(stderr).Count().IsEqualTo(3);
    }

    [Test]
    public async Task Test_FailedRun_Exits6_AndIsNoVerdict(CancellationToken cancellationToken)
    {
        var dotnet = Script("failing-dotnet",
            """
            #!/bin/sh
            echo "Failed: 1, Passed: 0"
            exit 3
            """,
            """
            @echo off
            echo Failed: 1, Passed: 0
            exit /b 3
            """);

        var run = await DotcovTest(cancellationToken, dotnet, "--min-line", "40");

        // 6, apart from 5 for a report dotcov could not read.
        await Assert.That(run.ExitCode).IsEqualTo(6);
        await Assert.That(run.StdOut).Contains("Failed: 1, Passed: 0");
        await Assert.That(run.StdErr).IsEqualTo(
            $"error: dotnet test exited with code 3; coverage was not evaluated.{Environment.NewLine}");
    }

    // JSON and markdown hold the dotnet test log back, so stdout stays a document; a failed run
    // shows it after the error line that is its outcome.
    [Test]
    [Arguments("json")]
    [Arguments("md")]
    public async Task Test_FailedRun_InADocumentFormat_ErrorFirst_ThenTheHeldBackLog(
        string format, CancellationToken cancellationToken)
    {
        var dotnet = Script("failing-dotnet",
            """
            #!/bin/sh
            echo "Failed: 1, Passed: 0"
            exit 3
            """,
            """
            @echo off
            echo Failed: 1, Passed: 0
            exit /b 3
            """);

        var run = await DotcovTest(cancellationToken, dotnet, "--min-line", "40", "--format", format);

        await Assert.That(run.ExitCode).IsEqualTo(6);
        await Assert.That(run.StdOut).IsEmpty();
        await Assert.That(run.StdErr).StartsWith(
            $"error: dotnet test exited with code 3; coverage was not evaluated.{Environment.NewLine}> ");
        await Assert.That(run.StdErr).Contains("Failed: 1, Passed: 0");
    }

    [Test]
    public async Task Test_RunWithoutAReport_IsNoData_WithoutPatternAdvice(CancellationToken cancellationToken)
    {
        // test takes no --pattern, so the empty-directory advice would name a flag it rejects.
        var dotnet = Script("silent-dotnet", "#!/bin/sh\nexit 0\n", "@exit /b 0\n");

        var run = await DotcovTest(cancellationToken, dotnet, "--min-line", "40");

        await Assert.That(run.ExitCode).IsEqualTo(DotCovCli.NoDataExitCode);
        var stderr = run.StdErr.Split(Environment.NewLine);
        await Assert.That(stderr[0]).StartsWith("NODATA: ");
        await Assert.That(stderr[1]).Matches(ReportsLine());
        await Assert.That(run.StdErr).DoesNotContain("warning: no file");
    }

    [Test]
    public async Task Test_HelpAfterTheSeparator_GoesToDotnetTest(CancellationToken cancellationToken)
    {
        var dotnet = FakeDotnet.Write(_ws, HalfCovered());

        var run = await DotcovTest(cancellationToken, dotnet, "--min-line", "40", "--", "--help");

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.Output);
        await Assert.That(run.StdOut.Split(Environment.NewLine)[0]).EndsWith(" --help");
        await Assert.That(run.StdOut).DoesNotContain("Options:");
    }

    // ── Colour: on in a CI log, off with --no-color or NO_COLOR ──

    [Test]
    [Arguments("report")]
    [Arguments("crap")]
    [Arguments("diff")]
    public async Task CiTable_IsColoured_UnlessNoColorOrNO_COLOR(string command, CancellationToken cancellationToken)
    {
        var report = HalfCovered();
        string[] args = command is "diff" ? [command, report, report] : [command, report];

        var coloured = await Dotcov(cancellationToken, Ci(), args);
        var flag = await Dotcov(cancellationToken, Ci(), [.. args, "--no-color"]);
        var variable = await Dotcov(cancellationToken, Ci(("NO_COLOR", "1")), args);

        await Assert.That(AnsiStrip.ContainsAnsi(coloured.StdOut)).IsTrue().Because(coloured.StdOut);
        foreach (var plain in new[] { flag, variable })
        {
            await Assert.That(plain.ExitCode).IsEqualTo(coloured.ExitCode);
            await Assert.That(AnsiStrip.ContainsAnsi(plain.Output)).IsFalse().Because(plain.Output);
            await Assert.That(plain.StdOut).IsEqualTo(AnsiStrip.From(coloured.StdOut));
        }
    }

    [Test]
    public async Task Test_NoColor_ReachesDotnetTest_AsNO_COLOR(CancellationToken cancellationToken)
    {
        var fake = FakeDotnet.Write(_ws, HalfCovered());
        var dotnet = Script("colour-dotnet",
            $"""
            #!/bin/sh
            if [ -z "$NO_COLOR" ]; then printf '\033[32mdotnet colour\033[0m\n'; fi
            exec "{fake}" "$@"
            """,
            $"""
            @echo off
            if not defined NO_COLOR echo {'\e'}[32mdotnet colour{'\e'}[0m
            call "{fake}" %*
            exit /b %errorlevel%
            """);

        var coloured = await Dotcov(cancellationToken, Ci(("DOTNET_HOST_PATH", dotnet)), "test", "--min-line", "40");
        var plain = await Dotcov(cancellationToken, Ci(("DOTNET_HOST_PATH", dotnet)), "test", "--min-line", "40", "--no-color");

        await Assert.That(coloured.ExitCode).IsEqualTo(0).Because(coloured.Output);
        await Assert.That(coloured.StdOut).Contains("\e[32mdotnet colour");
        await Assert.That(plain.ExitCode).IsEqualTo(0).Because(plain.Output);
        await Assert.That(plain.StdOut).Contains("src/A.cs");
        await Assert.That(AnsiStrip.ContainsAnsi(plain.Output)).IsFalse().Because(plain.Output);
    }
}
