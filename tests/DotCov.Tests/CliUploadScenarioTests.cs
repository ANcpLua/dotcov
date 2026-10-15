using System.Net;
using System.Text.Json;
using DotCov.Tests.Infrastructure;
using DotCov.Tool;

namespace DotCov.Tests;
[TestClass]
[TestCategory("Integration")]
public sealed class CliUploadScenarioTests(TestContext testContext) : IDisposable
{
    private readonly TempWorkspace _workspace = TempWorkspace.Create("dotcov-upload-");
    private readonly UploadEndpointFixture _endpoint = new();
    [TestInitialize]
    public Task InitializeEndpoint() => _endpoint.InitializeAsync();
    [TestCleanup]
    public async Task CleanupEndpoint() => await _endpoint.DisposeAsync();
    public void Dispose() => _workspace.Dispose();
    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task Report_UploadsJsonToTheRequestedEndpoint()
    {
        var cancellationToken = testContext.CancellationToken;
        _endpoint.RespondWith(HttpStatusCode.Created);
        var(code, output, error) = await Run("report", Report(), "--format", "json");
        var request = await _endpoint.Received.WaitAsync(cancellationToken);
        Assert.AreEqual(0, code, message: error);
        Assert.AreEqual("POST", request.Method);
        Assert.AreEqual("/coverage", request.Path);
        Assert.AreEqual("application/json; charset=utf-8", request.Headers["Content-Type"]);
        using var json = JsonDocument.Parse(request.Body);
        Assert.AreEqual(50, json.RootElement.GetProperty("summary").GetProperty("lineRate").GetDouble());
        Assert.AreEqual("src/Größe.cs", json.RootElement.GetProperty("files")[0].GetProperty("path").GetString());
        Assert.AreEqual(output, request.Body);
        Assert.Contains("Uploaded to", error);
    }

    // The no-match hint lists the directory after the upload. A directory deleted meanwhile
    // loses the hint, not the gate's exit code.
    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task Check_EmptyDirectoryDeletedDuringUpload_KeepsNoData()
    {
        var cancellationToken = testContext.CancellationToken;
        var empty = _workspace.CreateDirectory("vanishing");
        var running = Run("check", empty, "--min-line", "40");
        await _endpoint.Received.WaitAsync(cancellationToken);
        Directory.Delete(empty);
        _endpoint.RespondWith(HttpStatusCode.Created);
        var(code, _, error) = await running;
        Assert.AreEqual(3, code, message: error);
        Assert.StartsWith("NODATA:", error);
        Assert.Contains($"warning: no file in '{empty}' matches", error);
    }

    [TestMethod]
    [DataRow("40", HttpStatusCode.Created, 0, "PASS:", false)]
    [DataRow("80", HttpStatusCode.Created, 1, "FAIL:", false)]
    [DataRow("40", HttpStatusCode.InternalServerError, 5, "PASS:", false)]
    [DataRow("80", HttpStatusCode.InternalServerError, 1, "FAIL:", false)]
    [DataRow("0", HttpStatusCode.Created, 4, "DISABLED:", false)]
    [DataRow("0", HttpStatusCode.InternalServerError, 4, "DISABLED:", false)]
    [DataRow("40", HttpStatusCode.Created, 3, "NODATA:", true)]
    [DataRow("40", HttpStatusCode.InternalServerError, 3, "NODATA:", true)]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task Check_GateAndUploadOutcomes_PreserveTheExitContract(string threshold, HttpStatusCode status, int expectedCode, string verdict, bool empty)
    {
        var cancellationToken = testContext.CancellationToken;
        _endpoint.RespondWith(status);
        var(code, output, error) = await Run("check", empty ? _workspace.CreateDirectory("empty") : Report(), "--min-line", threshold);
        var request = await _endpoint.Received.WaitAsync(cancellationToken);
        Assert.AreEqual(expectedCode, code, message: error);
        Assert.Contains(verdict, output + error);
        Assert.Contains(status == HttpStatusCode.Created ? "Uploaded to" : "Upload failed", error);
        using var json = JsonDocument.Parse(request.Body);
        Assert.AreEqual("check", json.RootElement.GetProperty("command").GetString());
        // The uploaded document carries the same verdict and exit code as the gate that ran.
        var gate = json.RootElement.GetProperty("gate");
        Assert.StartsWith(verdict, gate.GetProperty("reason").GetString());
        Assert.AreEqual(expectedCode is 5 ? 0 : expectedCode, gate.GetProperty("exitCode").GetInt32());
        Assert.AreEqual(empty ? 0 : 1, json.RootElement.GetProperty("files").GetArrayLength());
        if (!empty)
            Assert.AreEqual(50, json.RootElement.GetProperty("summary").GetProperty("lineRate").GetDouble());
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task Check_JsonFormat_UploadsExactlyTheDocumentItPrints()
    {
        var cancellationToken = testContext.CancellationToken;
        _endpoint.RespondWith(HttpStatusCode.Created);
        var(code, output, error) = await Run("check", Report(), "--min-line", "80", "--format", "json");
        var request = await _endpoint.Received.WaitAsync(cancellationToken);
        Assert.AreEqual(1, code, message: error);
        Assert.AreEqual(output, request.Body);
        Assert.StartsWith("FAIL:", error);
    }

    [TestMethod]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task Snapshot_ServerDisconnectsAfterReceivingBody_PreservesTheLocalSnapshotAndReportsFailure()
    {
        var cancellationToken = testContext.CancellationToken;
        _endpoint.DisconnectAfterRequest();
        var(code, output, error) = await Run("snapshot", Report(), "--commit", "abc123", "--branch", "main", "--project", "dotcov");
        var request = await _endpoint.Received.WaitAsync(cancellationToken);
        Assert.AreEqual(5, code);
        Assert.Contains("Upload failed", error);
        Assert.DoesNotContain("Unhandled exception", error);
        Assert.AreEqual(output, request.Body);
        using var json = JsonDocument.Parse(request.Body);
        Assert.AreEqual("abc123", json.RootElement.GetProperty("commit").GetString());
        Assert.AreEqual("main", json.RootElement.GetProperty("branch").GetString());
    }

    private string Report() => _workspace.Write("coverage.cobertura.xml", Cobertura.NewDoc().AddClass("src/Größe.cs", c => c.Line(1, 1).Line(2, 0)));
    private async Task<(int Code, string Output, string Error)> Run(params string[] args)
    {
        await using var output = new StringWriter();
        await using var error = new StringWriter();
        var code = await DotCovCli.RunAsync([..args, "--upload", _endpoint.Url.AbsoluteUri], output, error);
        return (code, output.ToString(), error.ToString());
    }
}
