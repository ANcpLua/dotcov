using DotCov.Tests.Infrastructure;
using DotCov.Tool;

namespace DotCov.Tests;
/// <summary>
/// `dotcov report --format md --threshold N` end to end: the threshold line shows the value the
/// badge was judged against, unrounded, so 79.6 never prints "Threshold: 80%" next to a ✅ for
/// 79.7% coverage.
/// </summary>
[TestClass]
public sealed class CliReportThresholdTests : IDisposable
{
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-report-threshold-");
    public void Dispose() => _ws.Dispose();
    [TestMethod]
    [DataRow("79.6", "✅")]
    [DataRow("79.8", "❌")]
    public async Task Report_FractionalThreshold_IsRenderedAsGiven(string threshold, string badge)
    {
        var path = _ws.Write("797.cobertura.xml", Cobertura.NewDoc().AddClass("src/X.cs", c =>
        {
            for (var i = 1; i <= 1000; i++)
                c.Line(i, hits: i <= 797 ? 1 : 0);
        }));
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await DotCovCli.RunAsync(["report", path, "--format", "md", "--threshold", threshold], stdout, stderr);
        Assert.AreEqual(0, code, message: stderr.ToString());
        Assert.Contains($"## Coverage Report {badge}", stdout.ToString());
        Assert.Contains("**Line coverage:** 79.7%", stdout.ToString());
        Assert.Contains($"**Threshold:** {threshold}%", stdout.ToString());
    }
}
