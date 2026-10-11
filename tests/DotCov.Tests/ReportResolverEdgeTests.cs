using DotCov.Tests.Infrastructure;
using TUnit.Assertions.Enums;

namespace DotCov.Tests;

/// <summary>
/// Report discovery must mean the same thing on every host: a pattern's file-name portion
/// matches case-insensitively everywhere, as it already did on Windows and macOS, so the
/// same artifact tree resolves to the same reports on a Linux CI runner.
/// </summary>
public sealed class ReportResolverEdgeTests : IDisposable
{
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-resolver-edge-");

    public void Dispose() => _ws.Dispose();

    private static Cobertura Doc(string file) => Cobertura.NewDoc().AddClass(file, c => c.Line(1, 1));

    [Test]
    public async Task Resolve_DefaultPattern_FindsCapitalizedCoberturaName()
    {
        // ReportGenerator writes its merged Cobertura output as "Cobertura.xml". A case-
        // sensitive match left it unfound on Linux only - a silent NoData on one platform.
        var report = _ws.Write("merged/Cobertura.xml", Doc("a.cs"));

        var input = await Assert.That(ReportResolver.Resolve(_ws.Root)).HasSingleItem();

        await Assert.That(input.SourceName).IsEqualTo(report);
    }

    [Test]
    public async Task ResolveDirectory_ExplicitPattern_MatchesFileNameIgnoringCase()
    {
        var upper = _ws.Write("Coverage.Cobertura.XML", Doc("a.cs"));
        var lower = _ws.Write("nested/coverage.cobertura.xml", Doc("b.cs"));
        _ws.Write("coverage.json", "{}");

        var inputs = ReportResolver.ResolveDirectory(_ws.Root, ReportPattern.Parse("**/coverage.cobertura.xml"));

        await Assert.That(inputs.Select(i => i.SourceName))
            .IsEquivalentTo(new[] { upper, lower }.Order(StringComparer.Ordinal), CollectionOrdering.Matching);
    }

    [Test]
    public async Task ResolveDirectory_CaseInsensitiveMatch_StillHonorsWildcardsAndExtension()
    {
        var match = _ws.Write("TEST-RUN.Cobertura.xml", Doc("a.cs"));
        _ws.Write("cobertura.json", "{}");
        _ws.Write("report.xml", Doc("b.cs"));

        var inputs = ReportResolver.ResolveDirectory(_ws.Root, ReportPattern.Default);

        var input = await Assert.That(inputs).HasSingleItem();
        await Assert.That(input.SourceName).IsEqualTo(match);
    }
}
