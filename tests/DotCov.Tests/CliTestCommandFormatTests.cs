using System.Text.Json;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;
/// <summary>
/// `dotcov test --format` through the real tool process, with a stand-in dotnet that drops a
/// prepared report into the run directory. JSON and markdown keep stdout to the document and stderr
/// to the verdict, so the dotnet test log goes to the run directory and is shown only when the run
/// fails; the gate, offenders, and exit code are check's.
/// </summary>
[TestClass]
[ResourceLock(ProcessState.Environment)]
[TestCategory("Integration")]
public sealed class CliTestCommandFormatTests(TestContext testContext) : IDisposable
{
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-test-format-");
    public void Dispose() => _ws.Dispose();
    private Task<ChildRun> DotcovTest(CancellationToken cancellationToken, params string[] args)
    {
        var report = _ws.Write("fixture/report.xml", Cobertura.NewDoc().AddClass("src/A.cs", c => c.Line(1, hits: 1).Line(2, hits: 0).Line(3, hits: 0).Branch(4, "50% (1/2)")));
        return DotcovProcess.RunAsync(cancellationToken, _ws.Root, new Dictionary<string, string?> { ["DOTNET_HOST_PATH"] = FakeDotnet.Write(_ws, report) }, ["test", ..args]);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Json_StdoutIsTheGateDocument_TheVerdictLeadsStderr()
    {
        var cancellationToken = testContext.CancellationToken;
        var run = await DotcovTest(cancellationToken, "--min-line", "80", "--format", "json");
        Assert.AreEqual(1, run.ExitCode, message: run.Output);
        var root = JsonDocument.Parse(run.StdOut).RootElement;
        Assert.AreEqual("test", root.GetProperty("command").GetString());
        Assert.AreEqual(1, root.GetProperty("inputs").GetArrayLength());
        var gate = root.GetProperty("gate");
        Assert.AreEqual("fail", gate.GetProperty("outcome").GetString());
        Assert.AreEqual(1, gate.GetProperty("exitCode").GetInt32());
        var offender = gate.GetProperty("offenders").EnumerateArray().Single();
        Assert.AreEqual("2-3", offender.GetProperty("uncovered").GetString());
        Assert.StartsWith("FAIL: line 50.0% (min 80%)", run.StdErr);
        Assert.DoesNotContain("> ", run.StdErr);
        // The held-back log stays in the run directory for whoever wants it.
        var log = Directory.GetFiles(Path.Combine(_ws.Root, "TestResults"), "dotnet-test.log", SearchOption.AllDirectories);
        Assert.StartsWith("> ", await File.ReadAllTextAsync(log.Single(), cancellationToken));
        Assert.DoesNotContain("files below", run.StdErr);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Markdown_StdoutIsTheSummary_WithItsOffenders()
    {
        var cancellationToken = testContext.CancellationToken;
        var run = await DotcovTest(cancellationToken, "--min-line", "80", "--format", "md");
        Assert.AreEqual(1, run.ExitCode, message: run.Output);
        Assert.StartsWith("## Coverage Report ❌", run.StdOut);
        Assert.Contains("- `src/A.cs` — 50.0% lines, 50.0% branches — uncovered 2-3 · branches 4 (1/2)", run.StdOut);
        Assert.StartsWith("FAIL: line 50.0% (min 80%)", run.StdErr);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Table_KeepsTheTestLogAndTableOnStdout_AndListsOffendersOnStderr()
    {
        var cancellationToken = testContext.CancellationToken;
        var run = await DotcovTest(cancellationToken, "--min-line", "80", "--top", "5");
        Assert.AreEqual(1, run.ExitCode, message: run.Output);
        Assert.StartsWith("> ", run.StdOut);
        Assert.Contains("TOTAL", run.StdOut);
        Assert.StartsWith("FAIL: line 50.0% (min 80%)", run.StdErr);
        Assert.Contains("files below line threshold:" + Environment.NewLine + "  src/A.cs  50.0% lines  50.0% branches" + Environment.NewLine + "    uncovered 2-3 · branches 4 (1/2)", run.StdErr);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Json_Pass_IsTheWholeStdout_TheVerdictLeadsStderr()
    {
        var cancellationToken = testContext.CancellationToken;
        var run = await DotcovTest(cancellationToken, "--min-line", "40", "--format", "json");
        Assert.AreEqual(0, run.ExitCode, message: run.Output);
        Assert.AreEqual("pass", JsonDocument.Parse(run.StdOut).RootElement.GetProperty("gate").GetProperty("outcome").GetString());
        Assert.StartsWith("PASS: line 50.0% (min 40%)", run.StdErr);
    }
}
