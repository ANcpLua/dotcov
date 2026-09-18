using System.Globalization;
using System.IO.Pipes;
using DotCov.Fallout;
using Fallout.Common;
using Fallout.Components;
using Serilog;

/// <summary>The plain consumer: no ICompile, so ReportCoverage must run on its own.</summary>
class Build : FalloutBuild, ICoverageReport
{
    public static async Task<int> Main()
    {
        switch (Environment.GetEnvironmentVariable("DOTCOV_TESTBUILD"))
        {
            case "wait":
                await WaitForCancellationAsync();
                return 0;
            case "compile":
                return Execute<CompileBuild>(x => ((ICoverageReport)x).ReportCoverage);
            default:
                return Execute<Build>(x => ((ICoverageReport)x).ReportCoverage);
        }
    }

    private static async Task WaitForCancellationAsync()
    {
        await using var ready = new NamedPipeClientStream(".",
            Environment.GetEnvironmentVariable("DOTCOV_TESTBUILD_READY_PIPE")!,
            PipeDirection.Out, PipeOptions.Asynchronous);
        await ready.ConnectAsync(10_000);

        await using var writer = new StreamWriter(ready);
        await writer.WriteLineAsync(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        await writer.FlushAsync();
        await Task.Delay(Timeout.InfiniteTimeSpan);
    }
}

/// <summary>A consumer that also implements ICompile: the loose dependency must schedule Compile first.</summary>
class CompileBuild : FalloutBuild, ICoverageReport, ICompile
{
    Target ICompile.Compile => static d => d
        .Executes(static () => Log.Information("compile-target-ran"));
}
