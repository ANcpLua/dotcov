using DotCov.Tests.Infrastructure;
using DotCov.Tool;

namespace DotCov.Tests;

/// <summary>
/// `dotcov report --format md --threshold N` end to end: the threshold line shows the value the
/// badge was judged against. It used to round to an integer, so 79.6 printed "Threshold: 80%"
/// next to a ✅ for 79.7% coverage.
/// </summary>
public sealed class CliReportThresholdTests : IDisposable
{
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-report-threshold-");

    public void Dispose() => _ws.Dispose();

    [Test]
    [Arguments("79.6", "✅")]
    [Arguments("79.8", "❌")]
    public async Task Report_FractionalThreshold_IsRenderedAsGiven(string threshold, string badge)
    {
        var path = _ws.Write("797.cobertura.xml", Cobertura.NewDoc().AddClass("src/X.cs", c =>
        {
            for (var i = 1; i <= 1000; i++) c.Line(i, hits: i <= 797 ? 1 : 0);
        }));
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = await DotCovCli.RunAsync(["report", path, "--format", "md", "--threshold", threshold], stdout, stderr);

        await Assert.That(code).IsEqualTo(0).Because(stderr.ToString());
        await Assert.That(stdout.ToString()).Contains($"## Coverage Report {badge}");
        await Assert.That(stdout.ToString()).Contains("**Line coverage:** 79.7%");
        await Assert.That(stdout.ToString()).Contains($"**Threshold:** {threshold}%");
    }
}
