using System.Diagnostics;

namespace DotCov.Tests.Infrastructure;

/// <summary>
/// The dotcov tool as a real child process in a working directory of the test's choosing, with
/// an explicit environment: for contracts that depend on the working directory or on
/// <c>DOTNET_HOST_PATH</c>, which an in-process run would have to change for the whole test host.
/// </summary>
internal static class DotcovProcess
{
    public static Task<ChildRun> RunAsync(
        CancellationToken cancellationToken, string workingDirectory,
        IReadOnlyDictionary<string, string?> environment, params string[] args)
    {
        var directory = AppContext.BaseDirectory;
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
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
        start.Environment.Remove("DOTNET_TEST_RUNNER");
        start.Environment["NO_COLOR"] = "1";
        foreach (var (name, value) in environment)
            if (value is null) start.Environment.Remove(name); else start.Environment[name] = value;

        return ChildProcess.RunAsync(start, cancellationToken);
    }
}
