using System.Diagnostics;
using System.Reflection;
using DotCov.Tests.Infrastructure;
using DotCov.Tool;

namespace DotCov.Tests;
/// <summary>
/// Pins `dotcov test` through the real tool process. Like `dotnet test`, the command reads its
/// working directory, DOTNET_TEST_RUNNER, and the nearest global.json, so each test starts dotcov
/// in a workspace of its own with an explicit environment instead of changing the test host's.
/// Most runs name a project that does not exist: dotnet test fails fast, and the echoed command
/// line shows which runner's coverage options were chosen.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[ResourceLock(ProcessState.Environment)]
public sealed class CliTestCommandTests(TestContext testContext) : IDisposable
{
    private const string TestingPlatformCoverage = "--coverage --coverage-output-format cobertura";
    private const string VsTestCoverage = "--collect \"Code Coverage;Format=cobertura\"";
    private const string TestingPlatformGlobalJson = """{ "test": { "runner": "Microsoft.Testing.Platform" } }""";
    // A space in every path: arguments reach dotnet test as a list, never as a split string.
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov test command-");
    public void Dispose() => _ws.Dispose();
    // ── Runner selection: the coverage options of the runner dotnet test will use ──
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task WithoutGlobalJson_CollectsWithTheVSTestCollector_IntoAFreshResultsDirectory()
    {
        var cancellationToken = testContext.CancellationToken;
        var run = await Dotcov(cancellationToken, "test", "missing.csproj", "--min-line", "80");
        Assert.Contains("test missing.csproj --results-directory \"", run.StdOut);
        Assert.Contains($"{Created("TestResults")}\" {VsTestCoverage}", run.StdOut);
        AssertNotEvaluated(run);
    }

    [TestMethod]
    [DataRow("App.Tests.csproj", "--project")]
    [DataRow("App.sln", "--solution")]
    [DataRow("App.slnx", "--solution")]
    [DataRow("App.slnf", "--solution")]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task GlobalJsonTestingPlatform_UsesTheCoverageExtension(string target, string option)
    {
        var cancellationToken = testContext.CancellationToken;
        // Comments are skipped and the runner name is case-insensitive, as in the SDK.
        _ws.Write("global.json", """
            {
              // dotnet test reads test.runner from the nearest global.json
              "test": { "runner": "microsoft.testing.platform" }
            }
            """);
        var run = await Dotcov(cancellationToken, "test", target, "--min-line", "80");
        Assert.Contains($"test {option} {target} --results-directory ", run.StdOut);
        Assert.Contains(TestingPlatformCoverage, run.StdOut);
        AssertNotEvaluated(run);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task GlobalJsonInAParentDirectory_SelectsTheRunner_WithoutATarget()
    {
        var cancellationToken = testContext.CancellationToken;
        _ws.Write("global.json", TestingPlatformGlobalJson);
        var run = await Dotcov(cancellationToken, _ws.CreateDirectory("src/App.Tests"), new Dictionary<string, string?>(), "test", "--min-line", "80");
        Assert.Contains("test --results-directory \"", run.StdOut);
        Assert.Contains($"{Created("src/App.Tests/TestResults")}\" {TestingPlatformCoverage}", run.StdOut);
        AssertNotEvaluated(run);
    }

    [TestMethod]
    [DataRow("{ }")]
    [DataRow("""{ "test": { "runner": 5 } }""")]
    [DataRow("""{ "test": """)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task GlobalJsonWithoutAStringRunner_FallsBackToVSTest(string globalJson)
    {
        var cancellationToken = testContext.CancellationToken;
        _ws.Write("global.json", globalJson);
        var run = await Dotcov(cancellationToken, "test", "missing.csproj", "--min-line", "80");
        Assert.Contains(VsTestCoverage, run.StdOut);
        AssertNotEvaluated(run);
    }

    [TestMethod]
    [DataRow("Microsoft.Testing.Platform", """{ "test": { "runner": "VSTest" } }""", TestingPlatformCoverage)]
    [DataRow(" vstest ", TestingPlatformGlobalJson, VsTestCoverage)]
    [DataRow("custom-runner", TestingPlatformGlobalJson, TestingPlatformCoverage)]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task DotnetTestRunnerVariable_WinsOverGlobalJson_WhenRecognized(string runner, string globalJson, string coverage)
    {
        var cancellationToken = testContext.CancellationToken;
        _ws.Write("global.json", globalJson);
        var run = await Dotcov(cancellationToken, _ws.Root, new Dictionary<string, string?> { ["DOTNET_TEST_RUNNER"] = runner }, "test", "missing.csproj", "--min-line", "80");
        Assert.Contains(coverage, run.StdOut);
        AssertNotEvaluated(run);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ExistingStartupHook_IsRetainedAndCanCoexistWithTheEmbeddedHook()
    {
        var hook = Path.Combine(AppContext.BaseDirectory, "DotCov.CoverageHook.dll");
        var run = await Dotcov(testContext.CancellationToken, _ws.Root,
            new Dictionary<string, string?> { ["DOTNET_STARTUP_HOOKS"] = hook },
            "test", "missing.csproj", "--min-line", "80");
        AssertNotEvaluated(run);
        Assert.Contains("does not exist", run.StdOut);
        Assert.DoesNotContain("startup hook", run.StdOut.ToLowerInvariant());
    }

    // ── What reaches dotnet test ──
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ArgumentsAfterTheSeparator_FollowTheCoverageOptionsUnchanged()
    {
        var cancellationToken = testContext.CancellationToken;
        var run = await Dotcov(cancellationToken, "test", "missing.csproj", "--min-line", "90", "--", "-c", "Release", "--filter", "Category=Slow Tests", "--min-line");
        Assert.Contains($"{VsTestCoverage} -c Release --filter \"Category=Slow Tests\" --min-line{Environment.NewLine}", run.StdOut);
        AssertNotEvaluated(run);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task DotnetThatCannotStart_IsAnError()
    {
        var cancellationToken = testContext.CancellationToken;
        // DOTNET_HOST_PATH, which the SDK sets for the tools it runs, names the dotnet to start.
        var dotnet = _ws.PathOf("no-sdk/dotnet");
        var run = await Dotcov(cancellationToken, _ws.Root, new Dictionary<string, string?> { ["DOTNET_HOST_PATH"] = dotnet }, "test", "--min-line", "80");
        Assert.AreEqual(5, run.ExitCode);
        Assert.StartsWith($"> \"{dotnet}\" test --results-directory ", run.StdOut);
        Assert.StartsWith("error: ", run.StdErr);
        Assert.Contains(dotnet, run.StdErr);
        Assert.DoesNotContain("   at ", run.StdErr);
    }

    // ── A real run: Microsoft.Testing.Platform with the coverage extension MSTest brings ──
    [TestMethod]
    [Timeout(300_000, CooperativeCancellation = true)]
    [DataRow("30", 0, "PASS:", "brightgreen")]
    [DataRow("100", 1, "FAIL:", "red")]
    [DataRow("0", 4, "DISABLED:", "yellow")]
    public async Task TestingPlatformProject_IsTestedReportedAndGated(string minLine, int expectedCode, string verdict, string badgeColor)
    {
        var cancellationToken = testContext.CancellationToken;
        var project = WriteTestingPlatformProject();
        var summary = _ws.PathOf("step-summary.md");
        var run = await Dotcov(cancellationToken, _ws.Root, new Dictionary<string, string?> { ["GITHUB_STEP_SUMMARY"] = summary, // Leave no compiler server or MSBuild node holding files in the workspace.
 ["UseSharedCompilation"] = "false", ["MSBUILDDISABLENODEREUSE"] = "1" }, "test", project, "--min-line", minLine, "--exclude-generated", "--github-summary", "--badge-dir", "badge");
        Assert.AreEqual(expectedCode, run.ExitCode, message: run.Output);
        Assert.Contains($"test --project {project} --results-directory ", run.StdOut);
        Assert.Contains("Sign.cs", run.StdOut);
        Assert.DoesNotContain(verdict, run.StdOut);
        // Every outcome: the verdict, the run's reports relative to the working directory, then
        // the diagnostics - here the exclusion --exclude-generated applied.
        var stderr = run.StdErr.Split(Environment.NewLine);
        Assert.StartsWith(verdict, stderr[0]);
        Assert.MatchesRegex(@"^reports: TestResults[/\\][0-9a-f]{32}$", stderr[1]);
        Assert.IsTrue(Directory.Exists(_ws.PathOf(stderr[1]["reports: ".Length..])));
        Assert.Contains("exclusion rules: .g.cs, .designer.cs, /obj/", run.StdErr);
        Assert.Contains("excluded ", run.StdErr);
        Assert.Contains($"`{verdict}", await File.ReadAllTextAsync(summary, cancellationToken));
        // --badge-dir is relative to the working directory, and the badge carries the same verdict.
        Assert.Contains($"\"color\":\"{badgeColor}\"", await File.ReadAllTextAsync(_ws.PathOf("badge/coverage-badge.json"), cancellationToken));
        Assert.Contains($"<title>Coverage: {verdict}", await File.ReadAllTextAsync(_ws.PathOf("badge/coverage-badge.svg"), cancellationToken));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task SuccessfulRunWithoutCoverage_ExitsNoData()
    {
        var cancellationToken = testContext.CancellationToken;
        // dotnet test accepts a library with no tests and exits 0, but produces no coverage.
        // That child success must not become a passing coverage gate.
        _ws.Write("Library.csproj", $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net{Environment.Version.Major}.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        var run = await Dotcov(cancellationToken, "test", "Library.csproj", "--min-line", "80");
        Assert.AreEqual(3, run.ExitCode, message: run.Output);
        Assert.StartsWith("NODATA:", run.StdErr);
        Assert.DoesNotContain("PASS:", run.Output);
    }

    private string WriteTestingPlatformProject()
    {
        var framework = $"net{Environment.Version.Major}.{Environment.Version.Minor}";
        // The MSTest this suite restored: its packages, Microsoft.Testing.Extensions.CodeCoverage
        // among them, are already in the local package cache.
        var mstest = typeof(TestMethodAttribute).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
        _ws.Write("global.json", TestingPlatformGlobalJson);
        _ws.Write("Calc/Calc.csproj", $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>{{framework}}</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        _ws.Write("Calc/Sign.cs", """
            namespace Calc;

            public static class Sign
            {
                public static string Of(int value)
                {
                    if (value > 0) return "positive";
                    if (value < 0) return "negative";
                    return "zero";
                }
            }
            """);
        _ws.Write("Calc.Tests/Calc.Tests.csproj", $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>{{framework}}</TargetFramework>
                <OutputType>Exe</OutputType>
                <EnableMSTestRunner>true</EnableMSTestRunner>
                <TestingPlatformDotnetTestSupport>true</TestingPlatformDotnetTestSupport>
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="MSTest" Version="{{mstest}}" />
                <ProjectReference Include="../Calc/Calc.csproj" />
              </ItemGroup>
            </Project>
            """);
        _ws.Write("Calc.Tests/SignTests.cs", """
            using Microsoft.VisualStudio.TestTools.UnitTesting;

            namespace Calc.Tests;

            [TestClass]
            public class SignTests
            {
                [TestMethod]
                public void Positive() => Assert.AreEqual("positive", Sign.Of(1));
            }
            """);
        return Path.Combine("Calc.Tests", "Calc.Tests.csproj");
    }

    /// <summary>
    /// The end of the path of the one run directory dotcov created under <paramref name = "testResults"/>.
    /// Only the end is compared: the tool sees its working directory through realpath, so on macOS
    /// its absolute paths start /private/var where the workspace's start /var.
    /// </summary>
    private string Created(string testResults)
    {
        var run = Directory.GetDirectories(_ws.PathOf(testResults)).Single();
        return Path.DirectorySeparatorChar + Path.Combine("TestResults", Path.GetFileName(run));
    }

    /// <summary>A failed dotnet test run: exit 6, kept apart from 5 for a report dotcov could not read.</summary>
    private static void AssertNotEvaluated(ChildRun run)
    {
        Assert.AreEqual(DotCovCli.TestRunFailedExitCode, run.ExitCode);
        Assert.MatchesRegex(@"^error: dotnet test exited with code \d+; coverage was not evaluated\.\r?\n$", run.StdErr);
        Assert.DoesNotContain("PASS:", run.Output);
    }

    private Task<ChildRun> Dotcov(CancellationToken cancellationToken, params string[] args) => Dotcov(cancellationToken, _ws.Root, new Dictionary<string, string?>(), args);
    private static async Task<ChildRun> Dotcov(CancellationToken cancellationToken, string workingDirectory, IReadOnlyDictionary<string, string?> environment, params string[] args)
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
            "exec",
            "--depsfile",
            Path.Combine(directory, "DotCov.Tests.deps.json"),
            "--runtimeconfig",
            Path.Combine(directory, "DotCov.Tests.runtimeconfig.json"),
            Path.Combine(directory, "DotCov.Tool.dll")
        }.Concat(args))
            start.ArgumentList.Add(argument);
        // The outer test platform's handshake variables would tell an inner MTP run that it is
        // already a test host under a controller.
        foreach (var name in start.Environment.Keys.Where(static name => name.StartsWith("TESTINGPLATFORM_", StringComparison.OrdinalIgnoreCase)).ToList())
            start.Environment.Remove(name);
        // An SDK's MSBuild locations, if the outer dotnet test left them set, would steer the inner
        // SDK to the outer one's targets.
        foreach (var name in new[]
        {
            "MSBuildSDKsPath",
            "MSBuildExtensionsPath",
            "MSBUILD_EXE_PATH"
        }

        )
            start.Environment.Remove(name);
        start.Environment.Remove("DOTNET_TEST_RUNNER");
        start.Environment.Remove("GITHUB_STEP_SUMMARY");
        start.Environment["NO_COLOR"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        foreach (var(name, value)in environment)
            if (value is null)
                start.Environment.Remove(name);
            else
                start.Environment[name] = value;
        return await ChildProcess.RunAsync(start, cancellationToken);
    }
}
