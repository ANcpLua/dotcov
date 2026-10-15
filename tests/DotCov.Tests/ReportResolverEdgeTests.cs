using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;
/// <summary>
/// Report discovery must mean the same thing on every host: a pattern's file-name portion
/// matches case-insensitively on every OS, so the same artifact tree resolves to the same
/// reports on a Linux CI runner as on Windows and macOS.
/// </summary>
[TestClass]
public sealed class ReportResolverEdgeTests : IDisposable
{
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-resolver-edge-");
    public void Dispose() => _ws.Dispose();
    private static Cobertura Doc(string file) => Cobertura.NewDoc().AddClass(file, c => c.Line(1, 1));
    [TestMethod]
    public void Resolve_DefaultPattern_FindsCapitalizedCoberturaName()
    {
        // ReportGenerator writes its merged Cobertura output as "Cobertura.xml"; a case-sensitive
        // match would miss it on Linux only, a silent NoData on one platform.
        var report = _ws.Write("merged/Cobertura.xml", Doc("a.cs"));
        var input = Assert.ContainsSingle(ReportResolver.Resolve(_ws.Root));
        Assert.AreEqual(report, input.SourceName);
    }

    [TestMethod]
    public void ResolveDirectory_ExplicitPattern_MatchesFileNameIgnoringCase()
    {
        var upper = _ws.Write("Coverage.Cobertura.XML", Doc("a.cs"));
        var lower = _ws.Write("nested/coverage.cobertura.xml", Doc("b.cs"));
        _ws.Write("coverage.json", "{}");
        var inputs = ReportResolver.ResolveDirectory(_ws.Root, ReportPattern.Parse("**/coverage.cobertura.xml"));
        Assert.AreSequenceEqual(new[] { upper, lower }.Order(StringComparer.Ordinal), inputs.Select(i => i.SourceName), SequenceOrder.InOrder);
    }

    [TestMethod]
    public void ResolveDirectory_CaseInsensitiveMatch_StillHonorsWildcardsAndExtension()
    {
        var match = _ws.Write("TEST-RUN.Cobertura.xml", Doc("a.cs"));
        _ws.Write("cobertura.json", "{}");
        _ws.Write("report.xml", Doc("b.cs"));
        var inputs = ReportResolver.ResolveDirectory(_ws.Root, ReportPattern.Default);
        var input = Assert.ContainsSingle(inputs);
        Assert.AreEqual(match, input.SourceName);
    }
}
