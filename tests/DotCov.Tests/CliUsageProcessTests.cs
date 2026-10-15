using System.Diagnostics;
using DotCov.Tests.Infrastructure;
using DotCov.Tool;

namespace DotCov.Tests;
[TestClass]
[ResourceLock(ProcessState.Environment)]
[TestCategory("Integration")]
public sealed class CliUsageProcessTests(TestContext testContext) : IDisposable
{
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-usage-");
    public void Dispose() => _ws.Dispose();
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task BothVersionForms_PrintTheSameVersion()
    {
        var cancellationToken = testContext.CancellationToken;
        var command = await Run(cancellationToken, "version");
        var alias = await Run(cancellationToken, "--version");
        Assert.AreEqual(0, command.ExitCode);
        Assert.AreEqual(0, alias.ExitCode);
        Assert.AreEqual(command.StdOut, alias.StdOut);
        Assert.AreEqual($"dotcov {typeof(CoberturaParser).Assembly.GetName().Version!.ToString(3)}{Environment.NewLine}", alias.StdOut);
        Assert.IsEmpty(alias.StdErr);
        Assert.IsEmpty(command.StdErr);
    }

    [TestMethod]
    [DataRow("report")]
    [DataRow("check")]
    [DataRow("crap")]
    [DataRow("snapshot")]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task MissingArgument_AndSuppliedNonexistentPath_HaveDistinctExitCodes(string command)
    {
        var cancellationToken = testContext.CancellationToken;
        // check has no default threshold: the nonexistent path must be the only thing wrong.
        string[] threshold = command is "check" ? ["--min-line", "80"] : [];
        var missing = await Run(cancellationToken, command);
        var nonexistent = await Run(cancellationToken, [command, _ws.PathOf("absent.xml"), ..threshold]);
        Assert.AreEqual(DotCovCli.UsageExitCode, missing.ExitCode);
        Assert.AreEqual(DotCovCli.ErrorExitCode, nonexistent.ExitCode);
        Assert.IsEmpty(missing.StdOut);
        Assert.IsEmpty(nonexistent.StdOut);
        Assert.StartsWith("error: missing path", missing.StdErr);
        Assert.Contains(_ws.PathOf("absent.xml"), nonexistent.StdErr);
    }

    [TestMethod]
    [DataRow("version")]
    [DataRow("--version")]
    [DataRow("help")]
    [DataRow("--help")]
    [DataRow("-h")]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task NoInputCommands_RejectUnusedArguments(string command)
    {
        var cancellationToken = testContext.CancellationToken;
        foreach (var extra in new[]
        {
            new[]
            {
                "extra"
            },
            new[]
            {
                "--verbose"
            },
            new[]
            {
                "--"
            }
        }

        )
        {
            var result = await Run(cancellationToken, [command, ..extra]);
            Assert.AreEqual(2, result.ExitCode);
            Assert.IsEmpty(result.StdOut);
            Assert.StartsWith("error:", result.StdErr);
        }
    }

    [TestMethod]
    [DataRow("test", "", null, null)]
    [DataRow("report", "-q", null, null)]
    [DataRow("crap", "absent.xml", "--metrics", "")]
    [DataRow("report", "absent.xml", "--upload", "")]
    [DataRow("report", "absent.xml", "--upload", "https://")]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task InvalidArguments_AreRejectedBeforeExecution(string first, string second, string? flag, string? value)
    {
        var cancellationToken = testContext.CancellationToken;
        var args = flag is null ? new[]
        {
            first,
            second
        }

        : new[]
        {
            first,
            second,
            flag,
            value!
        };
        var result = await Run(cancellationToken, args);
        Assert.AreEqual(2, result.ExitCode);
        Assert.IsEmpty(result.StdOut);
        Assert.StartsWith("error:", result.StdErr);
        Assert.IsFalse(Directory.Exists(_ws.PathOf("TestResults")));
    }

    private Task<ChildRun> Run(CancellationToken cancellationToken, params string[] args)
    {
        var directory = AppContext.BaseDirectory;
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _ws.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[]
        {
            "exec",
            "--depsfile",
            Path.Combine(directory, "DotCov.Tests.deps.json"),
            "--runtimeconfig",
            Path.Combine(directory, "DotCov.Tests.runtimeconfig.json"),
            Path.Combine(directory, "DotCov.Tool.dll")
        }.Concat(args))
            start.ArgumentList.Add(argument);
        start.Environment.Remove("GITHUB_STEP_SUMMARY");
        start.Environment["NO_COLOR"] = "1";
        return ChildProcess.RunAsync(start, cancellationToken);
    }
}
