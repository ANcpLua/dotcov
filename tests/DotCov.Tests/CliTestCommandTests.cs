using System.Diagnostics;
using System.Reflection;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

/// <summary>
/// Pins `dotcov test` through the real tool process. Like `dotnet test`, the command reads its
/// working directory, DOTNET_TEST_RUNNER, and the nearest global.json, so each test starts dotcov
/// in a workspace of its own with an explicit environment instead of changing the test host's.
/// Most runs name a project that does not exist: dotnet test fails fast, and the echoed command
/// line shows which runner's coverage options were chosen.
/// </summary>
[Category("Integration")]
[Timeout(60_000)]
public sealed class CliTestCommandTests : IDisposable
{
    private const string TestingPlatformCoverage = "--coverage --coverage-output-format cobertura";
    private const string VSTestCoverage = "--collect \"Code Coverage;Format=cobertura\"";
    private const string TestingPlatformGlobalJson = """{ "test": { "runner": "Microsoft.Testing.Platform" } }""";

    // A space in every path: arguments reach dotnet test as a list, never as a split string.
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov test command-");

    public void Dispose() => _ws.Dispose();

    // ── Runner selection: the coverage options of the runner dotnet test will use ──

    [Test]
    public async Task WithoutGlobalJson_CollectsWithTheVSTestCollector_IntoAFreshResultsDirectory(
        CancellationToken cancellationToken)
    {
        var run = await Dotcov(cancellationToken, "test", "missing.csproj");

        await Assert.That(run.StdOut).Contains("test missing.csproj --results-directory \"");
        await Assert.That(run.StdOut).Contains($"{Created("TestResults")}\" {VSTestCoverage}");
        await AssertNotEvaluated(run);
    }

    [Test]
    [Arguments("App.Tests.csproj", "--project")]
    [Arguments("App.sln", "--solution")]
    [Arguments("App.slnx", "--solution")]
    [Arguments("App.slnf", "--solution")]
    public async Task GlobalJsonTestingPlatform_UsesTheCoverageExtension(
        string target, string option, CancellationToken cancellationToken)
    {
        // Comments are skipped and the runner name is case-insensitive, as in the SDK.
        _ws.Write("global.json", """
            {
              // dotnet test reads test.runner from the nearest global.json
              "test": { "runner": "microsoft.testing.platform" }
            }
            """);

        var run = await Dotcov(cancellationToken, "test", target);

        await Assert.That(run.StdOut).Contains($"test {option} {target} --results-directory ");
        await Assert.That(run.StdOut).Contains(TestingPlatformCoverage);
        await AssertNotEvaluated(run);
    }

    [Test]
    public async Task GlobalJsonInAParentDirectory_SelectsTheRunner_WithoutATarget(CancellationToken cancellationToken)
    {
        _ws.Write("global.json", TestingPlatformGlobalJson);

        var run = await Dotcov(cancellationToken, _ws.CreateDirectory("src/App.Tests"), new Dictionary<string, string?>(), "test");

        await Assert.That(run.StdOut).Contains("test --results-directory \"");
        await Assert.That(run.StdOut).Contains($"{Created("src/App.Tests/TestResults")}\" {TestingPlatformCoverage}");
        await AssertNotEvaluated(run);
    }

    [Test]
    [Arguments("{ }")]
    [Arguments("""{ "test": { "runner": 5 } }""")]
    [Arguments("""{ "test": """)]
    public async Task GlobalJsonWithoutAStringRunner_FallsBackToVSTest(string globalJson, CancellationToken cancellationToken)
    {
        _ws.Write("global.json", globalJson);

        var run = await Dotcov(cancellationToken, "test", "missing.csproj");

        await Assert.That(run.StdOut).Contains(VSTestCoverage);
        await AssertNotEvaluated(run);
    }

    [Test]
    [Arguments("Microsoft.Testing.Platform", """{ "test": { "runner": "VSTest" } }""", TestingPlatformCoverage)]
    [Arguments(" vstest ", TestingPlatformGlobalJson, VSTestCoverage)]
    [Arguments("xunit", TestingPlatformGlobalJson, TestingPlatformCoverage)]
    public async Task DotnetTestRunnerVariable_WinsOverGlobalJson_WhenRecognized(
        string runner, string globalJson, string coverage, CancellationToken cancellationToken)
    {
        _ws.Write("global.json", globalJson);

        var run = await Dotcov(cancellationToken, _ws.Root,
            new Dictionary<string, string?> { ["DOTNET_TEST_RUNNER"] = runner }, "test", "missing.csproj");

        await Assert.That(run.StdOut).Contains(coverage);
        await AssertNotEvaluated(run);
    }

    // ── What reaches dotnet test ──

    [Test]
    public async Task ArgumentsAfterTheSeparator_FollowTheCoverageOptionsUnchanged(CancellationToken cancellationToken)
    {
        var run = await Dotcov(cancellationToken, "test", "missing.csproj", "--min-line", "90",
            "--", "-c", "Release", "--filter", "Category=Slow Tests", "--min-line");

        await Assert.That(run.StdOut).Contains($"{VSTestCoverage} -c Release --filter \"Category=Slow Tests\" --min-line{Environment.NewLine}");
        await AssertNotEvaluated(run);
    }

    [Test]
    public async Task DotnetThatCannotStart_IsAnError(CancellationToken cancellationToken)
    {
        // DOTNET_HOST_PATH, which the SDK sets for the tools it runs, names the dotnet to start.
        var dotnet = _ws.PathOf("no-sdk/dotnet");

        var run = await Dotcov(cancellationToken, _ws.Root,
            new Dictionary<string, string?> { ["DOTNET_HOST_PATH"] = dotnet }, "test");

        await Assert.That(run.ExitCode).IsEqualTo(1);
        await Assert.That(run.StdOut).StartsWith($"> \"{dotnet}\" test --results-directory ");
        await Assert.That(run.StdErr).StartsWith("error: ");
        await Assert.That(run.StdErr).Contains(dotnet);
        await Assert.That(run.StdErr).DoesNotContain("   at ");
    }

    // ── A real run: Microsoft.Testing.Platform with the coverage extension TUnit brings ──

    [Test]
    [Timeout(300_000)]
    public async Task TestingPlatformProject_IsTestedReportedAndGated(CancellationToken cancellationToken)
    {
        var project = WriteTestingPlatformProject();
        var summary = _ws.PathOf("step-summary.md");

        var run = await Dotcov(cancellationToken, _ws.Root,
            new Dictionary<string, string?>
            {
                ["GITHUB_STEP_SUMMARY"] = summary,
                // Leave no compiler server or MSBuild node holding files in the workspace.
                ["UseSharedCompilation"] = "false",
                ["MSBUILDDISABLENODEREUSE"] = "1"
            },
            "test", project, "--min-line", "30", "--exclude-generated", "--github-summary");

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.Output);
        await Assert.That(run.StdOut).Contains($"test --project {project} --results-directory ");
        await Assert.That(run.StdOut).Contains("Sign.cs");
        await Assert.That(run.StdOut).Contains("PASS: line ");
        // Whatever dotnet test printed went to stdout; stderr carries only dotcov's own lines.
        await Assert.That(run.StdErr).IsEmpty();
        await Assert.That(await File.ReadAllTextAsync(summary, cancellationToken)).Contains("`PASS: line ");
    }

    private string WriteTestingPlatformProject()
    {
        var framework = $"net{Environment.Version.Major}.{Environment.Version.Minor}";
        // The TUnit this suite restored: its packages, Microsoft.Testing.Extensions.CodeCoverage
        // among them, are already in the local package cache.
        var tunit = typeof(TestAttribute).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];

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
                <ImplicitUsings>enable</ImplicitUsings>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="TUnit" Version="{{tunit}}" />
                <ProjectReference Include="../Calc/Calc.csproj" />
              </ItemGroup>
            </Project>
            """);
        _ws.Write("Calc.Tests/SignTests.cs", """
            namespace Calc.Tests;

            public class SignTests
            {
                [Test]
                public async Task Positive() => await Assert.That(Sign.Of(1)).IsEqualTo("positive");
            }
            """);
        return Path.Combine("Calc.Tests", "Calc.Tests.csproj");
    }

    /// <summary>
    /// The end of the path of the one run directory dotcov created under <paramref name="testResults"/>.
    /// Only the end is compared: the tool sees its working directory through realpath, so on macOS
    /// its absolute paths start /private/var where the workspace's start /var.
    /// </summary>
    private string Created(string testResults)
    {
        var run = Directory.GetDirectories(_ws.PathOf(testResults)).Single();
        return Path.DirectorySeparatorChar + Path.Combine("TestResults", Path.GetFileName(run));
    }

    private static async Task AssertNotEvaluated(ChildRun run)
    {
        await Assert.That(run.ExitCode).IsEqualTo(1);
        await Assert.That(run.StdErr).StartsWith("error: dotnet test exited with code ");
        await Assert.That(run.StdErr).Contains("coverage was not evaluated.");
        await Assert.That(run.Output).DoesNotContain("PASS:");
    }

    private Task<ChildRun> Dotcov(CancellationToken cancellationToken, params string[] args) =>
        Dotcov(cancellationToken, _ws.Root, new Dictionary<string, string?>(), args);

    private static async Task<ChildRun> Dotcov(CancellationToken cancellationToken, string workingDirectory,
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

        // The outer test platform's handshake variables would tell an inner MTP run that it is
        // already a test host under a controller.
        foreach (var name in start.Environment.Keys
                     .Where(static name => name.StartsWith("TESTINGPLATFORM_", StringComparison.OrdinalIgnoreCase))
                     .ToList())
            start.Environment.Remove(name);

        // An SDK's MSBuild locations, if the outer dotnet test left them set, would steer the inner
        // SDK to the outer one's targets.
        foreach (var name in new[] { "MSBuildSDKsPath", "MSBuildExtensionsPath", "MSBUILD_EXE_PATH" })
            start.Environment.Remove(name);

        start.Environment.Remove("DOTNET_TEST_RUNNER");
        start.Environment.Remove("GITHUB_STEP_SUMMARY");
        start.Environment["NO_COLOR"] = "1";
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        foreach (var (name, value) in environment)
            if (value is null) start.Environment.Remove(name); else start.Environment[name] = value;

        return await ChildProcess.RunAsync(start, cancellationToken);
    }
}
