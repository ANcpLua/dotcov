using System.Diagnostics;
using System.Text.Json;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;
/// <summary>
/// Runs the composite action's script (<c>.github/actions/badge/badge.sh</c>) as a real bash
/// process against a local bare repository standing in for <c>origin</c>, with this build's dotcov:
/// the orphan bootstrap, publishing only when the badge's contents change, the exit-code policy,
/// and that the consumer's clone keeps its branches and config. Windows uses Git Bash,
/// matching the composite action's shell rather than a possible WSL bash on PATH.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class BadgeActionTests : IDisposable
{
    private readonly TestContext _testContext;
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-badge-action-");
    private readonly string _origin;
    private readonly string _clone;
    private readonly string _report;
    private string _bash = "bash";
    public BadgeActionTests(TestContext testContext)
    {
        _testContext = testContext;
        _origin = _ws.CreateDirectory("origin.git");
        _clone = _ws.CreateDirectory("clone");
        _report = _ws.Write("reports/coverage.cobertura.xml", Cobertura.NewDoc().AddClass("src/A.cs", c => c.Line(1, hits: 1).Branch(2, "50% (1/2)")));
    }

    public void Dispose() => _ws.Dispose();
    private sealed record ActionRun(int ExitCode, string Output, IReadOnlyDictionary<string, string> Outputs);
    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Publish_BootstrapsAnOrphanBranch_ThenCommitsOnlyWhenTheBadgeChanges()
    {
        var cancellationToken = _testContext.CancellationToken;
        await Init(cancellationToken);
        var first = await RunAction(cancellationToken, ("min-branch", "40"));
        var repeated = await RunAction(cancellationToken, ("min-branch", "40"));
        // The same rates against a raised branch minimum: the number stays, the verdict changes.
        var raised = await RunAction(cancellationToken, ("min-branch", "60"));
        Assert.AreEqual(0, first.ExitCode, message: first.Output);
        Assert.Contains("The badge on 'badges' is unchanged: 100.0% / 50.0% (brightgreen).", repeated.Output);
        Assert.AreEqual(1, raised.ExitCode, message: raised.Output);
        Assert.AreEqual("1", raised.Outputs["exit-code"]);
        // Two commits: the orphan root, then the red badge on top of it.
        var log = await Git(_origin, cancellationToken, "log", "--format=%s|%P", "badges");
        var root = await Git(_origin, cancellationToken, "rev-parse", "badges~1");
        Assert.AreEqual($"badge: 100.0% / 50.0% (red) for 0123456|{root}\nbadge: 100.0% / 50.0% (brightgreen) for 0123456|", log);
        Assert.AreEqual("coverage-badge.json\ncoverage-badge.svg", await Git(_origin, cancellationToken, "ls-tree", "--name-only", "badges"));
        // The published files are the ones this run rendered, nothing else.
        var published = await Git(_origin, cancellationToken, "show", "badges:coverage-badge.json");
        var rendered = await File.ReadAllTextAsync(Path.Combine(raised.Outputs["directory"], "coverage-badge.json"), cancellationToken);
        Assert.AreEqual(rendered, published);
        using var json = JsonDocument.Parse(published);
        Assert.AreEqual("red", json.RootElement.GetProperty("color").GetString());
        // The consumer's clone gains no branch and no identity.
        Assert.IsEmpty(await Git(_clone, cancellationToken, "branch", "--list"));
        Assert.DoesNotContain("user.", await Git(_clone, cancellationToken, "config", "--local", "--list"));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task NoThreshold_FailsWithDotcovsUsageError_AndPublishesNothing()
    {
        var cancellationToken = _testContext.CancellationToken;
        // The action has no default threshold either: an implicit 80 would gate on a number
        // nobody chose.
        await Init(cancellationToken);
        var run = await RunAction(cancellationToken);
        Assert.AreEqual(2, run.ExitCode, message: run.Output);
        Assert.AreEqual("2", run.Outputs["exit-code"]);
        Assert.Contains("error: missing threshold: 'check' needs --min-line N, --min-branch N, or both", run.Output);
        Assert.Contains("dotcov check exited 2 without writing a badge; nothing was published.", run.Output);
        Assert.IsEmpty(await Git(_origin, cancellationToken, "branch", "--list"));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task FailOnGateFalse_KeepsGateOutcomesGreen()
    {
        var cancellationToken = _testContext.CancellationToken;
        await Init(cancellationToken);
        var run = await RunAction(cancellationToken, ("min-line", "100"), ("min-branch", "60"), ("fail-on-gate", "false"));
        Assert.AreEqual(0, run.ExitCode, message: run.Output);
        Assert.AreEqual("1", run.Outputs["exit-code"]);
        Assert.Contains("Published 100.0% / 50.0% (red) to 'badges'.", run.Output);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Error_FailsEvenWithFailOnGateFalse_AndPublishesNothing()
    {
        var cancellationToken = _testContext.CancellationToken;
        await Init(cancellationToken);
        var run = await RunAction(cancellationToken, ("reports", _ws.PathOf("missing")), ("min-line", "80"), ("fail-on-gate", "false"));
        Assert.AreEqual(5, run.ExitCode, message: run.Output);
        Assert.Contains("::error::dotcov check exited 5 without writing a badge; nothing was published.", run.Output);
        Assert.DoesNotContain("directory", run.Outputs.Keys);
        Assert.IsEmpty(await Git(_origin, cancellationToken, "branch", "--list"));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task PublishFalse_RendersWithoutPushing()
    {
        var cancellationToken = _testContext.CancellationToken;
        await Init(cancellationToken);
        var run = await RunAction(cancellationToken, ("min-line", "80"), ("publish", "false"));
        Assert.AreEqual(0, run.ExitCode, message: run.Output);
        Assert.IsTrue(File.Exists(Path.Combine(run.Outputs["directory"], "coverage-badge.svg")));
        Assert.IsEmpty(await Git(_origin, cancellationToken, "branch", "--list"));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task BadgeDirectory_PublishesTheRenderedBadge_WithoutRunningDotcov()
    {
        var cancellationToken = _testContext.CancellationToken;
        await Init(cancellationToken);
        var rendered = _ws.CreateDirectory("rendered");
        var svg = _ws.Write("rendered/coverage-badge.svg", "<svg/>");
        _ws.Write("rendered/coverage-badge.json", """{"schemaVersion":1,"label":"Coverage","message":"no data","color":"lightgrey"}""");
        // `false` as dotcov: running it would fail the step.
        var run = await RunAction(cancellationToken, ("reports", ""), ("badge-directory", rendered), ("command", "false"));
        Assert.AreEqual(0, run.ExitCode, message: run.Output);
        Assert.DoesNotContain("exit-code", run.Outputs.Keys);
        Assert.AreEqual(rendered, run.Outputs["directory"]);
        Assert.Contains("Published no data (lightgrey) to 'badges'.", run.Output);
        Assert.AreEqual(await File.ReadAllTextAsync(svg, cancellationToken), await Git(_origin, cancellationToken, "show", "badges:coverage-badge.svg"));
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task BadgeDirectory_WithoutABadge_FailsAndPublishesNothing()
    {
        var cancellationToken = _testContext.CancellationToken;
        await Init(cancellationToken);
        var empty = _ws.CreateDirectory("rendered");
        var run = await RunAction(cancellationToken, ("reports", ""), ("badge-directory", empty));
        Assert.AreEqual(1, run.ExitCode);
        Assert.Contains($"::error::badge-directory '{empty}' holds no coverage-badge.svg and coverage-badge.json; nothing was published.", run.Output);
        Assert.IsEmpty(await Git(_origin, cancellationToken, "branch", "--list"));
    }

    [TestMethod]
    [DataRow(true, "Set reports or badge-directory, not both.")]
    [DataRow(false, "Set reports, or badge-directory to publish a badge rendered earlier.")]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ReportsAndBadgeDirectory_TakeExactlyOne(bool both, string error)
    {
        var cancellationToken = _testContext.CancellationToken;
        await Init(cancellationToken);
        var run = await RunAction(cancellationToken, both ? [("badge-directory", _ws.CreateDirectory("rendered"))] : [("reports", "")]);
        Assert.AreEqual(1, run.ExitCode);
        Assert.Contains($"::error::{error}", run.Output);
        Assert.IsEmpty(run.Outputs);
    }

    [TestMethod]
    [DataRow("publish", "yes")]
    [DataRow("fail-on-gate", "1")]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task NonBooleanSwitch_FailsBeforeRunningDotcov(string input, string value)
    {
        var cancellationToken = _testContext.CancellationToken;
        await Init(cancellationToken);
        var run = await RunAction(cancellationToken, (input, value));
        Assert.AreEqual(1, run.ExitCode);
        Assert.Contains($"::error::{input} must be true or false, not '{value}'.", run.Output);
        Assert.IsEmpty(run.Outputs);
    }

    private async Task Init(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            // Git's exec path is <installation>/mingw64/libexec/git-core. Resolve its own
            // Bash instead of PATH's bash.exe, which can belong to WSL.
            var execPath = await Git(_ws.Root, cancellationToken, "--exec-path");
            _bash = Path.GetFullPath(Path.Combine(execPath, "..", "..", "..", "bin", "bash.exe"));
            if (!File.Exists(_bash))
                throw new FileNotFoundException("Badge action tests require Git for Windows' Bash.", _bash);
        }

        await Git(_origin, cancellationToken, "init", "--quiet", "--bare");
        await Git(_clone, cancellationToken, "init", "--quiet");
        await Git(_clone, cancellationToken, "remote", "add", "origin", _origin);
    }

    /// <summary>The script as action.yml runs it: every input as a DOTCOV_* variable, defaults included.</summary>
    private async Task<ActionRun> RunAction(CancellationToken cancellationToken, params (string Name, string Value)[] inputs)
    {
        var values = new Dictionary<string, string>
        {
            ["reports"] = _report,
            ["badge-directory"] = "",
            ["pattern"] = "",
            ["min-line"] = "",
            ["min-branch"] = "",
            ["args"] = "",
            ["branch"] = "badges",
            ["publish"] = "true",
            ["fail-on-gate"] = "true",
            ["version"] = "unused",
            ["command"] = DotcovCommand(),
        };
        foreach (var(name, value)in inputs)
            values[name] = value;
        var outputs = _ws.PathOf("github-output");
        await File.WriteAllTextAsync(outputs, "", cancellationToken);
        var start = Start(_bash, _clone, ShellPath(Path.Combine(AppContext.BaseDirectory, "BadgeAction", "badge.sh")));
        if (OperatingSystem.IsWindows())
            start.Environment["PATH"] = Path.GetDirectoryName(_bash) + Path.PathSeparator + start.Environment["PATH"];
        start.Environment["RUNNER_TEMP"] = _ws.CreateDirectory("runner-temp");
        start.Environment["GITHUB_OUTPUT"] = outputs;
        start.Environment["GITHUB_SHA"] = "0123456789abcdef";
        start.Environment.Remove("GITHUB_RUN_ID");
        foreach (var(name, value)in values)
            start.Environment[$"DOTCOV_{name.Replace('-', '_').ToUpperInvariant()}"] = value;
        var run = await ChildProcess.RunAsync(start, cancellationToken);
        var written = (await File.ReadAllLinesAsync(outputs, cancellationToken)).Select(static line => line.Split('=', 2)).ToDictionary(static pair => pair[0], static pair => pair[1]);
        return new ActionRun(run.ExitCode, run.Output, written);
    }

    /// <summary>
    /// This build's dotcov behind a wrapper: the command input is split on whitespace, and the
    /// test output directory may contain spaces.
    /// </summary>
    private string DotcovCommand()
    {
        var directory = AppContext.BaseDirectory;
        var wrapper = _ws.Write("dotcov", $"""
            exec dotnet exec --depsfile '{ShellPath(Path.Combine(directory, "DotCov.Tests.deps.json"))}' --runtimeconfig '{ShellPath(Path.Combine(directory, "DotCov.Tests.runtimeconfig.json"))}' '{ShellPath(Path.Combine(directory, "DotCov.Tool.dll"))}' "$@"
            """);
        return $"bash {ShellPath(wrapper)}";
    }

    private static async Task<string> Git(string directory, CancellationToken cancellationToken, params string[] args)
    {
        var run = await ChildProcess.RunAsync(Start("git", directory, args), cancellationToken);
        if (run.ExitCode is not 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} exited {run.ExitCode}: {run.Output}");
        return run.StdOut.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
    }

    private static string ShellPath(string path) => path.Replace('\\', '/');
    private static ProcessStartInfo Start(string fileName, string directory, params string[] args)
    {
        var start = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        // The host's git identity and config must not leak in: the script supplies its own identity.
        start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["NO_COLOR"] = "1";
        // The outer test platform's handshake variables would reach dotcov's own process.
        foreach (var name in start.Environment.Keys.Where(static name => name.StartsWith("TESTINGPLATFORM_", StringComparison.OrdinalIgnoreCase)).ToList())
            start.Environment.Remove(name);
        return start;
    }
}
