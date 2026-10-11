using System.Diagnostics;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

/// <summary>
/// `dotcov test` failures that happen before any test runs, observed through the real tool
/// process: the command creates its results directory under its working directory, which only
/// a process of its own can be given. The child's environment is copied from this process, so the
/// class shares the environment constraint of the tests that change it.
/// </summary>
[NotInParallel(ProcessState.Environment)]
[Category("Integration")]
[Timeout(60_000)]
public sealed class DotCovCliTestCommandEdgeTests : IDisposable
{
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-test-edge-");

    public void Dispose() => _ws.Dispose();

    [Test]
    public async Task ResultsDirectoryThatCannotBeCreated_IsAnError_BeforeDotnetTestStarts(CancellationToken cancellationToken)
    {
        // IOException: a file named TestResults occupies the place of the run directory.
        _ws.Write("TestResults", "not a directory");

        var run = await Dotcov(cancellationToken, "test", "missing.csproj");

        await Assert.That(run.ExitCode).IsEqualTo(1);
        // No echoed "> dotnet test" line: nothing was started.
        await Assert.That(run.StdOut).IsEmpty();
        await Assert.That(run.StdErr).StartsWith("error: ");
        await Assert.That(run.StdErr).Contains("TestResults");
        await Assert.That(run.StdErr).DoesNotContain("   at ");
    }

    private async Task<ChildRun> Dotcov(CancellationToken cancellationToken, params string[] args)
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

        start.Environment.Remove("DOTNET_TEST_RUNNER");
        start.Environment.Remove("GITHUB_STEP_SUMMARY");
        start.Environment["NO_COLOR"] = "1";
        return await ChildProcess.RunAsync(start, cancellationToken);
    }
}
