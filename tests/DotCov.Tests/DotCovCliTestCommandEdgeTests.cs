using System.Diagnostics;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;
/// <summary>
/// `dotcov test` failures that happen before any test runs, observed through the real tool
/// process: the command creates its results directory under its working directory, which only
/// a process of its own can be given. The child's environment is copied from this process, so the
/// class shares the environment constraint of the tests that change it.
/// </summary>
[TestClass]
[ResourceLock(ProcessState.Environment)]
[TestCategory("Integration")]
public sealed class DotCovCliTestCommandEdgeTests(TestContext testContext) : IDisposable
{
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-test-edge-");
    public void Dispose() => _ws.Dispose();
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ResultsDirectoryThatCannotBeCreated_IsAnError_BeforeDotnetTestStarts()
    {
        var cancellationToken = testContext.CancellationToken;
        // IOException: a file named TestResults occupies the place of the run directory.
        _ws.Write("TestResults", "not a directory");
        var run = await Dotcov(cancellationToken, "test", "missing.csproj", "--min-line", "80");
        Assert.AreEqual(5, run.ExitCode);
        // No echoed "> dotnet test" line: nothing was started.
        Assert.IsEmpty(run.StdOut);
        Assert.StartsWith("error: ", run.StdErr);
        Assert.Contains("TestResults", run.StdErr);
        Assert.DoesNotContain("   at ", run.StdErr);
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
            "exec",
            "--depsfile",
            Path.Combine(directory, "DotCov.Tests.deps.json"),
            "--runtimeconfig",
            Path.Combine(directory, "DotCov.Tests.runtimeconfig.json"),
            Path.Combine(directory, "DotCov.Tool.dll")
        }.Concat(args))
            start.ArgumentList.Add(argument);
        start.Environment.Remove("DOTNET_TEST_RUNNER");
        start.Environment.Remove("GITHUB_STEP_SUMMARY");
        start.Environment["NO_COLOR"] = "1";
        return await ChildProcess.RunAsync(start, cancellationToken);
    }
}
