using System.Diagnostics;
using System.Text.Json;
using DotCov.Tests.Infrastructure;
using TUnit.Core.Enums;

namespace DotCov.Tests;

/// <summary>
/// Runs the composite action's script (<c>.github/actions/badge/badge.sh</c>) as a real bash
/// process against a local bare repository standing in for <c>origin</c>, with this build's dotcov:
/// the orphan bootstrap, publishing only when the badge's contents change, the exit-code policy,
/// and that the consumer's clone keeps its branches and config. Linux and macOS only: on Windows,
/// <c>bash</c> on PATH can be WSL's rather than Git's.
/// </summary>
[RunOn(OS.Linux | OS.MacOs)]
[Category("Integration")]
[Timeout(60_000)]
public sealed class BadgeActionTests : IDisposable
{
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-badge-action-");
    private readonly string _origin;
    private readonly string _clone;
    private readonly string _report;

    public BadgeActionTests()
    {
        _origin = _ws.CreateDirectory("origin.git");
        _clone = _ws.CreateDirectory("clone");
        _report = _ws.Write("reports/coverage.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c.Line(1, hits: 1).Branch(2, "50% (1/2)")));
    }

    public void Dispose() => _ws.Dispose();

    private sealed record ActionRun(int ExitCode, string Output, IReadOnlyDictionary<string, string> Outputs);

    [Test]
    public async Task Publish_BootstrapsAnOrphanBranch_ThenCommitsOnlyWhenTheBadgeChanges(CancellationToken cancellationToken)
    {
        await Init(cancellationToken);

        var first = await RunAction(cancellationToken, ("min-branch", "40"));
        var repeated = await RunAction(cancellationToken, ("min-branch", "40"));
        // The same rates against a raised branch minimum: the number stays, the verdict changes.
        var raised = await RunAction(cancellationToken, ("min-branch", "60"));

        await Assert.That(first.ExitCode).IsEqualTo(0).Because(first.Output);
        await Assert.That(repeated.Output).Contains("The badge on 'badges' is unchanged: 100.0% / 50.0% (brightgreen).");
        await Assert.That(raised.ExitCode).IsEqualTo(1).Because(raised.Output);
        await Assert.That(raised.Outputs["exit-code"]).IsEqualTo("1");

        // Two commits: the orphan root, then the red badge on top of it.
        var log = await Git(cancellationToken, _origin, "log", "--format=%s|%P", "badges");
        var root = await Git(cancellationToken, _origin, "rev-parse", "badges~1");
        await Assert.That(log).IsEqualTo(
            $"badge: 100.0% / 50.0% (red) for 0123456|{root}\nbadge: 100.0% / 50.0% (brightgreen) for 0123456|");
        await Assert.That(await Git(cancellationToken, _origin, "ls-tree", "--name-only", "badges"))
            .IsEqualTo($"coverage-badge.json{"\n"}coverage-badge.svg");

        // The published files are the ones this run rendered, nothing else.
        var published = await Git(cancellationToken, _origin, "show", "badges:coverage-badge.json");
        var rendered = await File.ReadAllTextAsync(Path.Combine(raised.Outputs["directory"], "coverage-badge.json"), cancellationToken);
        await Assert.That(published).IsEqualTo(rendered);
        using var json = JsonDocument.Parse(published);
        await Assert.That(json.RootElement.GetProperty("color").GetString()).IsEqualTo("red");

        // The consumer's clone gains no branch and no identity.
        await Assert.That(await Git(cancellationToken, _clone, "branch", "--list")).IsEmpty();
        await Assert.That(await Git(cancellationToken, _clone, "config", "--local", "--list")).DoesNotContain("user.");
    }

    [Test]
    public async Task FailOnGateFalse_KeepsGateOutcomesGreen(CancellationToken cancellationToken)
    {
        await Init(cancellationToken);

        var run = await RunAction(cancellationToken, ("min-line", "100"), ("min-branch", "60"), ("fail-on-gate", "false"));

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.Output);
        await Assert.That(run.Outputs["exit-code"]).IsEqualTo("1");
        await Assert.That(run.Output).Contains("Published 100.0% / 50.0% (red) to 'badges'.");
    }

    [Test]
    public async Task Error_FailsEvenWithFailOnGateFalse_AndPublishesNothing(CancellationToken cancellationToken)
    {
        await Init(cancellationToken);

        var run = await RunAction(cancellationToken, ("reports", _ws.PathOf("missing")), ("fail-on-gate", "false"));

        await Assert.That(run.ExitCode).IsEqualTo(5).Because(run.Output);
        await Assert.That(run.Output).Contains("::error::dotcov check exited 5 without writing a badge; nothing was published.");
        await Assert.That(run.Outputs.ContainsKey("directory")).IsFalse();
        await Assert.That(await Git(cancellationToken, _origin, "branch", "--list")).IsEmpty();
    }

    [Test]
    public async Task PublishFalse_RendersWithoutPushing(CancellationToken cancellationToken)
    {
        await Init(cancellationToken);

        var run = await RunAction(cancellationToken, ("publish", "false"));

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.Output);
        await Assert.That(File.Exists(Path.Combine(run.Outputs["directory"], "coverage-badge.svg"))).IsTrue();
        await Assert.That(await Git(cancellationToken, _origin, "branch", "--list")).IsEmpty();
    }

    [Test]
    [Arguments("publish", "yes")]
    [Arguments("fail-on-gate", "1")]
    public async Task NonBooleanSwitch_FailsBeforeRunningDotcov(string input, string value, CancellationToken cancellationToken)
    {
        await Init(cancellationToken);

        var run = await RunAction(cancellationToken, (input, value));

        await Assert.That(run.ExitCode).IsEqualTo(1);
        await Assert.That(run.Output).Contains($"::error::{input} must be true or false, not '{value}'.");
        await Assert.That(run.Outputs).IsEmpty();
    }

    private async Task Init(CancellationToken cancellationToken)
    {
        await Git(cancellationToken, _origin, "init", "--quiet", "--bare");
        await Git(cancellationToken, _clone, "init", "--quiet");
        await Git(cancellationToken, _clone, "remote", "add", "origin", _origin);
    }

    /// <summary>The script as action.yml runs it: every input as a DOTCOV_* variable, defaults included.</summary>
    private async Task<ActionRun> RunAction(CancellationToken cancellationToken, params (string Name, string Value)[] inputs)
    {
        var values = new Dictionary<string, string>
        {
            ["reports"] = _report, ["pattern"] = "", ["min-line"] = "80", ["min-branch"] = "0", ["args"] = "",
            ["branch"] = "badges", ["publish"] = "true", ["fail-on-gate"] = "true", ["version"] = "unused",
            ["command"] = DotcovCommand(),
        };
        foreach (var (name, value) in inputs) values[name] = value;

        var outputs = _ws.PathOf("github-output");
        File.WriteAllText(outputs, "");
        var start = Start("bash", _clone, Path.Combine(AppContext.BaseDirectory, "BadgeAction", "badge.sh"));
        start.Environment["RUNNER_TEMP"] = _ws.CreateDirectory("runner-temp");
        start.Environment["GITHUB_OUTPUT"] = outputs;
        start.Environment["GITHUB_SHA"] = "0123456789abcdef";
        start.Environment.Remove("GITHUB_RUN_ID");
        foreach (var (name, value) in values)
            start.Environment[$"DOTCOV_{name.Replace('-', '_').ToUpperInvariant()}"] = value;

        var run = await ChildProcess.RunAsync(start, cancellationToken);
        var written = (await File.ReadAllLinesAsync(outputs, cancellationToken))
            .Select(static line => line.Split('=', 2))
            .ToDictionary(static pair => pair[0], static pair => pair[1]);
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
            exec dotnet exec --depsfile '{Path.Combine(directory, "DotCov.Tests.deps.json")}' --runtimeconfig '{Path.Combine(directory, "DotCov.Tests.runtimeconfig.json")}' '{Path.Combine(directory, "DotCov.Tool.dll")}' "$@"
            """);
        return $"bash {wrapper}";
    }

    private static async Task<string> Git(CancellationToken cancellationToken, string directory, params string[] args)
    {
        var run = await ChildProcess.RunAsync(Start("git", directory, args), cancellationToken);
        if (run.ExitCode is not 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} exited {run.ExitCode}: {run.Output}");
        return run.StdOut.TrimEnd('\n');
    }

    private static ProcessStartInfo Start(string fileName, string directory, params string[] args)
    {
        var start = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = directory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        // The host's git identity and config must not leak in: the script supplies its own identity.
        start.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["NO_COLOR"] = "1";
        // The outer test platform's handshake variables would reach dotcov's own process.
        foreach (var name in start.Environment.Keys
                     .Where(static name => name.StartsWith("TESTINGPLATFORM_", StringComparison.OrdinalIgnoreCase))
                     .ToList())
            start.Environment.Remove(name);
        return start;
    }
}
