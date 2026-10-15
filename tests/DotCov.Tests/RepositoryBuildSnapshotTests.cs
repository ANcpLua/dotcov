using System.Diagnostics;
using System.Text.Json;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

/// <summary>
/// The repository wrapper's Snapshot target, run as a real process against a real Git checkout.
/// A detached HEAD (CI checkouts of a pull request, bisect, a worktree on a commit) has no
/// branch, and the target must still produce a snapshot instead of crashing the build.
/// </summary>
[NotInParallel(ProcessState.Environment)]
[Category("Integration")]
[Timeout(60_000)]
public sealed class RepositoryBuildSnapshotTests : IDisposable
{
    private readonly TempWorkspace _workspace = TempWorkspace.Create("dotcov-snapshot-");

    public RepositoryBuildSnapshotTests() => _workspace.CreateDirectory(".fallout");

    public void Dispose() => _workspace.Dispose();

    [Test]
    public async Task Snapshot_DetachedHead_StampsTheCommitAndAnUnknownBranch(CancellationToken cancellationToken)
    {
        await Git(cancellationToken, "init", "--quiet");
        await Git(cancellationToken, "commit", "--quiet", "--allow-empty", "-m", "base");
        var commit = (await Git(cancellationToken, "rev-parse", "HEAD")).Trim();
        await Git(cancellationToken, "checkout", "--quiet", "--detach");
        var report = _workspace.Write("reports/dotcov.coverage.cobertura.1.xml",
            Cobertura.NewDoc().AddClass("src/Example.cs", c => c.Line(1, 1).Line(2, 0)));

        var run = await Build(cancellationToken, "Snapshot", "--reports", report);

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.Output);
        var snapshot = await Assert.That(
            Directory.GetFiles(_workspace.PathOf("artifacts"), "snapshot.json", SearchOption.AllDirectories)).HasSingleItem();
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(snapshot, cancellationToken));
        await Assert.That(json.RootElement.GetProperty("commit").GetString()).IsEqualTo(commit);
        await Assert.That(json.RootElement.GetProperty("branch").GetString()).IsEqualTo("unknown");
        await Assert.That(json.RootElement.GetProperty("project").GetString()).IsEqualTo("dotcov");
        await Assert.That(run.Output).Contains("--branch not provided; snapshot stamped 'unknown'");
    }

    [Test]
    public async Task Snapshot_OnABranch_StampsThatBranch(CancellationToken cancellationToken)
    {
        await Git(cancellationToken, "init", "--quiet", "--initial-branch", "feature/x");
        await Git(cancellationToken, "commit", "--quiet", "--allow-empty", "-m", "base");
        var report = _workspace.Write("reports/dotcov.coverage.cobertura.1.xml",
            Cobertura.NewDoc().AddClass("src/Example.cs", c => c.Line(1, 1)));

        var run = await Build(cancellationToken, "Snapshot", "--reports", report);

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.Output);
        var snapshot = Directory.GetFiles(_workspace.PathOf("artifacts"), "snapshot.json", SearchOption.AllDirectories);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(snapshot.Single(), cancellationToken));
        await Assert.That(json.RootElement.GetProperty("branch").GetString()).IsEqualTo("feature/x");
        await Assert.That(run.Output).DoesNotContain("not provided");
    }

    private async Task<string> Git(CancellationToken cancellationToken, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _workspace.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[]
                 {
                     "-c", "user.name=dotcov", "-c", "user.email=dotcov@example.invalid", "-c", "commit.gpgsign=false"
                 }.Concat(args))
            start.ArgumentList.Add(argument);
        start.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";

        var run = await ChildProcess.RunAsync(start, cancellationToken);
        if (run.ExitCode is not 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {run.Output}");
        return run.StdOut;
    }

    private async Task<ChildRun> Build(CancellationToken cancellationToken, params string[] args)
    {
        var directory = AppContext.BaseDirectory;
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = _workspace.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[]
                 {
                     "exec", "--depsfile", Path.Combine(directory, "DotCov.Tests.deps.json"),
                     "--runtimeconfig", Path.Combine(directory, "DotCov.Tests.runtimeconfig.json"),
                     Path.Combine(directory, "DotCov.Build.dll")
                 }.Concat(args).Concat(["--root", _workspace.Root, "--no-logo"]))
            start.ArgumentList.Add(argument);
        start.Environment.Remove("GITHUB_STEP_SUMMARY");
        // On GitHub Actions, Fallout's GitRepository takes branch and commit from GITHUB_REF and
        // GITHUB_SHA instead of the workspace repository these tests build.
        start.Environment.Remove("GITHUB_ACTIONS");
        start.Environment["NO_COLOR"] = "1";

        var run = await ChildProcess.RunAsync(start, cancellationToken);
        return run with { StdOut = AnsiStrip.From(run.StdOut), StdErr = AnsiStrip.From(run.StdErr) };
    }
}
