using System.Text.Json;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

/// <summary>
/// `dotcov test --format` through the real tool process, with a stand-in dotnet that drops a
/// prepared report into the run directory. JSON and markdown keep stdout to the document and stderr
/// to the verdict, so the dotnet test log goes to the run directory and is shown only when the run
/// fails; the gate, offenders, and exit code are check's.
/// </summary>
[NotInParallel(ProcessState.Environment)]
[Category("Integration")]
[Timeout(60_000)]
public sealed class CliTestCommandFormatTests : IDisposable
{
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-test-format-");

    public void Dispose() => _ws.Dispose();

    private Task<ChildRun> DotcovTest(CancellationToken cancellationToken, params string[] args)
    {
        var report = _ws.Write("fixture/report.xml", Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c.Line(1, hits: 1).Line(2, hits: 0).Line(3, hits: 0).Branch(4, "50% (1/2)")));
        return DotcovProcess.RunAsync(cancellationToken, _ws.Root,
            new Dictionary<string, string?> { ["DOTNET_HOST_PATH"] = FakeDotnet.Write(_ws, report) },
            ["test", .. args]);
    }

    [Test]
    public async Task Json_StdoutIsTheGateDocument_TheVerdictLeadsStderr(CancellationToken cancellationToken)
    {
        var run = await DotcovTest(cancellationToken, "--min-line", "80", "--format", "json");

        await Assert.That(run.ExitCode).IsEqualTo(1).Because(run.Output);
        var root = JsonDocument.Parse(run.StdOut).RootElement;
        await Assert.That(root.GetProperty("command").GetString()).IsEqualTo("test");
        await Assert.That(root.GetProperty("inputs").GetArrayLength()).IsEqualTo(1);
        var gate = root.GetProperty("gate");
        await Assert.That(gate.GetProperty("outcome").GetString()).IsEqualTo("fail");
        await Assert.That(gate.GetProperty("exitCode").GetInt32()).IsEqualTo(1);
        var offender = gate.GetProperty("offenders").EnumerateArray().Single();
        await Assert.That(offender.GetProperty("uncovered").GetString()).IsEqualTo("2-3");
        await Assert.That(run.StdErr).StartsWith("FAIL: line 50.0% (min 80%)");
        await Assert.That(run.StdErr).DoesNotContain("> ");
        // The held-back log stays in the run directory for whoever wants it.
        var log = Directory.GetFiles(Path.Combine(_ws.Root, "TestResults"), "dotnet-test.log", SearchOption.AllDirectories);
        await Assert.That(await File.ReadAllTextAsync(log.Single())).StartsWith("> ");
        await Assert.That(run.StdErr).DoesNotContain("files below");
    }

    [Test]
    public async Task Markdown_StdoutIsTheSummary_WithItsOffenders(CancellationToken cancellationToken)
    {
        var run = await DotcovTest(cancellationToken, "--min-line", "80", "--format", "md");

        await Assert.That(run.ExitCode).IsEqualTo(1).Because(run.Output);
        await Assert.That(run.StdOut).StartsWith("## Coverage Report ❌");
        await Assert.That(run.StdOut).Contains("- `src/A.cs` — 50.0% lines, 50.0% branches — uncovered 2-3 · branches 4 (1/2)");
        await Assert.That(run.StdErr).StartsWith("FAIL: line 50.0% (min 80%)");
    }

    [Test]
    public async Task Table_KeepsTheTestLogAndTableOnStdout_AndListsOffendersOnStderr(CancellationToken cancellationToken)
    {
        var run = await DotcovTest(cancellationToken, "--min-line", "80", "--top", "5");

        await Assert.That(run.ExitCode).IsEqualTo(1).Because(run.Output);
        await Assert.That(run.StdOut).StartsWith("> ");
        await Assert.That(run.StdOut).Contains("TOTAL");
        await Assert.That(run.StdErr).StartsWith("FAIL: line 50.0% (min 80%)");
        await Assert.That(run.StdErr).Contains("files below line threshold:" + Environment.NewLine +
            "  src/A.cs  50.0% lines  50.0% branches" + Environment.NewLine +
            "    uncovered 2-3 · branches 4 (1/2)");
    }

    [Test]
    public async Task Json_Pass_IsTheWholeStdout_TheVerdictLeadsStderr(CancellationToken cancellationToken)
    {
        var run = await DotcovTest(cancellationToken, "--min-line", "40", "--format", "json");

        await Assert.That(run.ExitCode).IsEqualTo(0).Because(run.Output);
        await Assert.That(JsonDocument.Parse(run.StdOut).RootElement.GetProperty("gate").GetProperty("outcome").GetString())
            .IsEqualTo("pass");
        await Assert.That(run.StdErr).StartsWith("PASS: line 50.0% (min 40%)");
    }
}
