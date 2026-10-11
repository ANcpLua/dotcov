using System.Xml;
using DotCov;
using DotCov.Tool;
using Fallout.Common;
using Fallout.Common.Git;
using Fallout.Common.IO;
using Serilog;
using static Fallout.Common.Tools.DotNet.DotNetTasks;

class Build : FalloutBuild
{
    public static int Main() => Execute<Build>(x => x.Test);

    [Parameter("Test configuration")]
    public string Configuration { get; set; } = "Release";

    [Parameter("TUnit tree-node filter")]
    public string Filter { get; set; } = "/*/*/*/*";

    [Parameter("Existing report file or directory; skips coverage collection")]
    public string? Reports { get; set; }

    [Parameter("Minimum line coverage")]
    public string MinLine { get; set; } = "100";

    [Parameter("Minimum branch coverage")]
    public string MinBranch { get; set; } = "100";

    [Parameter("Output format: table, json, md")]
    public string Format { get; set; } = "table";

    [Parameter("Maximum CRAP score")]
    public string MaxCrap { get; set; } = "30";

    [Parameter("Number of methods displayed; the CRAP gate still checks all methods")]
    public string Top { get; set; } = "20";

    [Parameter("Optional metrics XML for CRAP analysis")]
    public string? Metrics { get; set; }

    [Parameter("Baseline Cobertura file or directory for Diff")]
    public string? Before { get; set; }

    private readonly string _runId = Guid.NewGuid().ToString("N");
    private AbsolutePath ResultsDirectory => RootDirectory / "TestResults" / _runId;
    private AbsolutePath TestProject => RootDirectory / "tests" / "DotCov.Tests" / "DotCov.Tests.csproj";
    private string Input => Reports is null ? ResultsDirectory.ToString() : Path.GetFullPath(Reports, RootDirectory);

    Target Test => d => d
        .Description("Run TUnit tests; --filter selects a tree-node scope")
        .Executes(() => RunTests(coverage: false));

    Target CollectCoverage => d => d
        .Description("Run tests with Coverlet into an isolated results directory")
        .OnlyWhenStatic(() => Reports is null)
        .Executes(() => RunTests(coverage: true));

    Target Coverage => d => d
        .Description("Collect coverage, require every src project in it, and enforce line/branch thresholds")
        .DependsOn(CollectCoverage)
        .Executes(async () =>
        {
            AssertEverySourceProjectMeasured();
            await RunDotCov(["check", Input, "--min-line", MinLine, "--min-branch", MinBranch, "--github-summary"],
                failure: "Coverage must stay at 100% of lines and branches. For each file dotcov lists above, add a test that " +
                         "drives the uncovered line or branch through a public entry point (tests/CLAUDE.md), or delete the " +
                         "code if no input can reach it. Never exclude code from measurement.");
        });

    Target Report => d => d
        .Description("Collect and display coverage; --format selects table/json/md")
        .DependsOn(CollectCoverage)
        .Executes(() => RunDotCov(["report", Input, "--format", Format]));

    Target Crap => d => d
        .Description("Collect method coverage and enforce the CRAP gate")
        .DependsOn(CollectCoverage)
        .Executes(() =>
        {
            List<string> args = ["crap", Input, "--max-crap", MaxCrap, "--top", Top, "--format", Format, "--github-summary"];
            if (Metrics is not null)
                args.AddRange(["--metrics", Path.GetFullPath(Metrics, RootDirectory)]);
            return RunDotCov([.. args]);
        });

    Target Snapshot => d => d
        .Description("Write snapshot JSON with Git identity under artifacts/<run>/")
        .DependsOn(CollectCoverage)
        .Executes(async () =>
        {
            var repository = GitRepository.FromLocalDirectory(RootDirectory);
            var directory = RootDirectory / "artifacts" / _runId;
            Directory.CreateDirectory(directory);
            var path = directory / "snapshot.json";
            // A detached HEAD (pull-request checkouts, bisect, worktrees) has no branch, and a null
            // argument crashed the target; without --branch the CLI stamps 'unknown' and warns.
            List<string> args = ["snapshot", Input, "--commit", repository.Commit, "--project", "dotcov"];
            if (repository.Branch is { } branch)
                args.AddRange(["--branch", branch]);
            await using (var output = new StreamWriter(path))
                await RunDotCov([.. args], output);
            Log.Information("Snapshot: {Path}", path);
        });

    Target Diff => d => d
        .Description("Compare --before with freshly collected coverage or --reports")
        .Requires(() => Before)
        .DependsOn(CollectCoverage)
        .Executes(() => RunDotCov(["diff", Path.GetFullPath(Before!, RootDirectory), Input, "--format", Format]));

    private void RunTests(bool coverage)
    {
        DotNet($"test --project {TestProject} -c {Configuration} -p:Coverage={(coverage ? "true" : "false")} --results-directory {ResultsDirectory} --treenode-filter {Filter} --minimum-expected-tests 1 --no-ansi --progress off",
            workingDirectory: RootDirectory);
        Log.Information("Test results: {Path}", ResultsDirectory);
    }

    // A Cobertura package is one measured assembly. coverlet can drop an assembly without failing
    // (coverlet.MTP 10.1.0 excluded DotCov.dll while this gate passed on the other two), so every
    // src/ project needs a package before the thresholds count. Runs before `check` so a failure
    // never leaves a PASS summary behind; a missing input stays `check`'s to report.
    private void AssertEverySourceProjectMeasured()
    {
        var source = RootDirectory / "src";
        if (!source.DirectoryExists() || !Path.Exists(Input))
            return;

        var measured = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in ReportResolver.Resolve(Input))
        {
            using var reader = XmlReader.Create(input.OpenStream(), new XmlReaderSettings { CloseInput = true, DtdProcessing = DtdProcessing.Ignore });
            try
            {
                while (reader.ReadToFollowing("package"))
                    measured.Add(reader.GetAttribute("name") ?? "");
            }
            catch (XmlException ex)
            {
                Assert.Fail($"{input.SourceName}: {ex.Message}", ex);
            }
        }

        var missing = source.GlobFiles("*/*.csproj")
            .Select(project => project.NameWithoutExtension)
            .Where(name => !measured.Contains(name))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.True(missing.Length is 0, $"Coverage report has no package for: {string.Join(", ", missing)}");
    }

    private static async Task RunDotCov(string[] args, TextWriter? output = null, string? failure = null)
    {
        var exitCode = await DotCovCli.RunAsync(args, output ?? Console.Out, Console.Error);
        Assert.True(exitCode is 0, failure ?? $"dotcov {args[0]} exited with code {exitCode}");
    }
}
