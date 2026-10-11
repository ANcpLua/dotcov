using System.Diagnostics;
using DotCov.Tests.Infrastructure;
using DotCov.Tool;

namespace DotCov.Tests;

[NotInParallel(ProcessState.Environment)]
[Category("Integration")]
[Timeout(60_000)]
public sealed class CliUsageProcessTests : IDisposable
{
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-usage-");

    public void Dispose() => _ws.Dispose();

    [Test]
    public async Task BothVersionForms_PrintTheSameVersion(CancellationToken cancellationToken)
    {
        var command = await Run(cancellationToken, "version");
        var alias = await Run(cancellationToken, "--version");

        await Assert.That(command.ExitCode).IsEqualTo(0);
        await Assert.That(alias.ExitCode).IsEqualTo(0);
        await Assert.That(alias.StdOut).IsEqualTo(command.StdOut);
        await Assert.That(alias.StdOut).IsEqualTo($"dotcov {typeof(CoberturaParser).Assembly.GetName().Version}{Environment.NewLine}");
        await Assert.That(alias.StdErr).IsEmpty();
        await Assert.That(command.StdErr).IsEmpty();
    }

    [Test]
    [Arguments("report")]
    [Arguments("check")]
    [Arguments("crap")]
    [Arguments("snapshot")]
    public async Task MissingArgument_AndSuppliedNonexistentPath_HaveDistinctExitCodes(
        string command, CancellationToken cancellationToken)
    {
        var missing = await Run(cancellationToken, command);
        var nonexistent = await Run(cancellationToken, command, _ws.PathOf("absent.xml"));

        await Assert.That(missing.ExitCode).IsEqualTo(DotCovCli.UsageExitCode);
        await Assert.That(nonexistent.ExitCode).IsEqualTo(DotCovCli.ErrorExitCode);
        await Assert.That(missing.StdOut).IsEmpty();
        await Assert.That(nonexistent.StdOut).IsEmpty();
        await Assert.That(missing.StdErr).StartsWith("error: missing path");
        await Assert.That(nonexistent.StdErr).Contains(_ws.PathOf("absent.xml"));
    }

    [Test]
    [Arguments("version")]
    [Arguments("--version")]
    [Arguments("help")]
    [Arguments("--help")]
    [Arguments("-h")]
    public async Task NoInputCommands_RejectUnusedArguments(string command, CancellationToken cancellationToken)
    {
        foreach (var extra in new[] { new[] { "extra" }, new[] { "--verbose" }, new[] { "--" } })
        {
            var result = await Run(cancellationToken, [command, .. extra]);
            await Assert.That(result.ExitCode).IsEqualTo(2);
            await Assert.That(result.StdOut).IsEmpty();
            await Assert.That(result.StdErr).StartsWith("error:");
        }
    }

    [Test]
    [Arguments("test", "", null, null)]
    [Arguments("report", "-q", null, null)]
    [Arguments("crap", "absent.xml", "--metrics", "")]
    [Arguments("report", "absent.xml", "--upload", "")]
    [Arguments("report", "absent.xml", "--upload", "https://")]
    public async Task InvalidArguments_AreRejectedBeforeExecution(string first, string second,
        string? flag, string? value, CancellationToken cancellationToken)
    {
        var args = flag is null ? new[] { first, second } : new[] { first, second, flag, value! };
        var result = await Run(cancellationToken, args);
        await Assert.That(result.ExitCode).IsEqualTo(2);
        await Assert.That(result.StdOut).IsEmpty();
        await Assert.That(result.StdErr).StartsWith("error:");
        await Assert.That(Directory.Exists(_ws.PathOf("TestResults"))).IsFalse();
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
            "exec", "--depsfile", Path.Combine(directory, "DotCov.Tests.deps.json"),
            "--runtimeconfig", Path.Combine(directory, "DotCov.Tests.runtimeconfig.json"),
            Path.Combine(directory, "DotCov.Tool.dll")
        }.Concat(args))
            start.ArgumentList.Add(argument);
        start.Environment.Remove("GITHUB_STEP_SUMMARY");
        start.Environment["NO_COLOR"] = "1";
        return ChildProcess.RunAsync(start, cancellationToken);
    }
}
