using System.Net;
using System.Text.Json;
using DotCov.Tests.Infrastructure;
using DotCov.Tool;

namespace DotCov.Tests;

[ClassDataSource<UploadEndpointFixture>]
[Category("Integration")]
[Timeout(10_000)]
public sealed class CliUploadScenarioTests(UploadEndpointFixture endpoint) : IDisposable
{
    private readonly TempWorkspace _workspace = TempWorkspace.Create("dotcov-upload-");

    public void Dispose() => _workspace.Dispose();

    [Test]
    public async Task Report_UploadsJsonToTheRequestedEndpoint(CancellationToken cancellationToken)
    {
        endpoint.RespondWith(HttpStatusCode.Created);

        var (code, output, error) = await Run("report", Report(), "--format", "json");
        var request = await endpoint.Received.WaitAsync(cancellationToken);

        await Assert.That(code).IsEqualTo(0).Because(error);
        await Assert.That(request.Method).IsEqualTo("POST");
        await Assert.That(request.Path).IsEqualTo("/coverage");
        await Assert.That(request.Headers["Content-Type"]).IsEqualTo("application/json; charset=utf-8");
        using var json = JsonDocument.Parse(request.Body);
        await Assert.That(json.RootElement.GetProperty("summary").GetProperty("lineRate").GetDouble()).IsEqualTo(50);
        await Assert.That(json.RootElement.GetProperty("files")[0].GetProperty("path").GetString()).IsEqualTo("src/Größe.cs");
        await Assert.That(request.Body).IsEqualTo(output.TrimEnd());
        await Assert.That(error).Contains("Uploaded to");
    }

    [Test]
    [Arguments("40", HttpStatusCode.Created, 0, "PASS:")]
    [Arguments("80", HttpStatusCode.Created, 1, "FAIL:")]
    [Arguments("40", HttpStatusCode.InternalServerError, 1, "PASS:")]
    [Arguments("80", HttpStatusCode.InternalServerError, 1, "FAIL:")]
    public async Task Check_GateAndUploadOutcomes_PreserveTheExitContract(
        string threshold, HttpStatusCode status, int expectedCode, string verdict, CancellationToken cancellationToken)
    {
        endpoint.RespondWith(status);

        var (code, output, error) = await Run("check", Report(), "--min-line", threshold);
        var request = await endpoint.Received.WaitAsync(cancellationToken);

        await Assert.That(code).IsEqualTo(expectedCode).Because(error);
        await Assert.That(output + error).Contains(verdict);
        await Assert.That(error).Contains(status == HttpStatusCode.Created ? "Uploaded to" : "Upload failed");
        using var json = JsonDocument.Parse(request.Body);
        await Assert.That(json.RootElement.GetProperty("summary").GetProperty("lineRate").GetDouble()).IsEqualTo(50);
    }

    [Test]
    public async Task Snapshot_ServerDisconnectsAfterReceivingBody_PreservesTheLocalSnapshotAndReportsFailure(
        CancellationToken cancellationToken)
    {
        endpoint.DisconnectAfterRequest();

        var (code, output, error) = await Run("snapshot", Report(), "--commit", "abc123",
            "--branch", "main", "--project", "dotcov");
        var request = await endpoint.Received.WaitAsync(cancellationToken);

        await Assert.That(code).IsEqualTo(1);
        await Assert.That(error).Contains("Upload failed");
        await Assert.That(error).DoesNotContain("Unhandled exception");
        await Assert.That(request.Body).IsEqualTo(output);
        using var json = JsonDocument.Parse(request.Body);
        await Assert.That(json.RootElement.GetProperty("commit").GetString()).IsEqualTo("abc123");
        await Assert.That(json.RootElement.GetProperty("branch").GetString()).IsEqualTo("main");
    }

    private string Report() => _workspace.Write("coverage.cobertura.xml", Cobertura.NewDoc()
        .AddClass("src/Größe.cs", c => c.Line(1, 1).Line(2, 0)));

    private async Task<(int Code, string Output, string Error)> Run(params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await DotCovCli.RunAsync([.. args, "--upload", endpoint.Url.AbsoluteUri], output, error);
        return (code, output.ToString(), error.ToString());
    }
}
