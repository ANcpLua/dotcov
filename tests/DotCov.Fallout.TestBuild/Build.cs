using DotCov.Fallout;
using Fallout.Common;
using Fallout.Components;
using Serilog;

/// <summary>The plain consumer: no ICompile, so ReportCoverage must run on its own.</summary>
class Build : FalloutBuild, ICoverageReport
{
    public static int Main() =>
        Environment.GetEnvironmentVariable("DOTCOV_TESTBUILD") == "compile"
            ? Execute<CompileBuild>(x => ((ICoverageReport)x).ReportCoverage)
            : Execute<Build>(x => ((ICoverageReport)x).ReportCoverage);
}

/// <summary>A consumer that also implements ICompile: the loose dependency must schedule Compile first.</summary>
class CompileBuild : FalloutBuild, ICoverageReport, ICompile
{
    Target ICompile.Compile => static d => d
        .Executes(static () => Log.Information("compile-target-ran"));
}
