using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

/// <summary>
/// API-longevity hardening of the published structs and collection surfaces:
/// a <c>default(FileCoverage)</c>/<c>default(FileDelta)</c> (array growth, dictionary lookup
/// miss, bare <c>default</c>) must behave as an empty value instead of violating its own
/// non-nullable annotations, and the parser's internal accumulator dictionaries must not
/// escape mutable behind the <c>IReadOnly*</c> surface.
/// </summary>
public sealed class CoreApiHardeningTests
{
    [Test]
    public async Task DefaultFileCoverage_BehavesAsEmptyFile_NoNullReferences()
    {
        // Property initializers never run for default instances — new FileCoverage[1] used to
        // hold null LineHits/UncoveredLines and GetLineStatus threw NullReferenceException.
        var arr = new FileCoverage[1];
        var f = arr[0];

        await Assert.That(f.LineHits).IsEmpty();
        await Assert.That(f.BranchesByLine).IsEmpty();
        await Assert.That(f.ConditionsByLine).IsEmpty();
        await Assert.That(f.UncoveredLines).IsEmpty();
        await Assert.That(f.PartialBranches).IsEmpty();
        await Assert.That(f.GetLineStatus(1)).IsEqualTo(LineStatus.Miss);
        await Assert.That(f.TryGetLineStatus(1, out var status)).IsFalse();
        await Assert.That(status).IsEqualTo(LineStatus.Miss);
        await Assert.That(f.LineRate).IsNull();
    }

    [Test]
    public async Task DefaultFileCoverage_MergesLikeAnEmptyFile()
    {
        var measured = Reports.ClassifiedFile("a.cs", 1, 2, 0, 0,
            lineHits: new Dictionary<int, int> { [1] = 3, [2] = 0 },
            branchesByLine: new Dictionary<int, (int Covered, int Total)>());

        var (merged, warnings) = default(FileCoverage).MergeWith(measured);

        await Assert.That(warnings).IsEmpty();
        await Assert.That(merged.LinesTotal).IsEqualTo(2);
        await Assert.That(merged.LinesHit).IsEqualTo(1);
    }

    [Test]
    public async Task DefaultFileDelta_HasEmptyLineChanges()
    {
        var arr = new FileDelta[1];

        await Assert.That(arr[0].LineChanges).IsEmpty();
    }

    [Test]
    public async Task ParsedReport_CollectionSurfaces_AreNotTheMutableAccumulators()
    {
        // Materialize used to hand the LineAccumulator's live Dictionary instances straight to
        // the report: a downcast mutation desynchronized LineHits from the precomputed
        // LinesHit/StrictlyHitLines aggregates. The construction seam must wrap read-only.
        var file = Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c.Line(1, hits: 1).Branch(10, "50% (1/2)"))
            .Parse().Files[0];

        await Assert.That(file.LineHits).IsNotTypeOf<Dictionary<int, int>>();
        await Assert.That(file.BranchesByLine).IsNotTypeOf<Dictionary<int, (int Covered, int Total)>>();
        var hits = (await Assert.That(file.LineHits).IsAssignableTo<IDictionary<int, int>>())!;
        Assert.ThrowsExactly<NotSupportedException>(() => hits[9999] = 1);
    }

    [Test]
    public async Task MergedFile_CollectionSurfaces_AreNotMutable()
    {
        // Same seam on the merge path: MergeWith builds fresh dicts, and they must leave the
        // method read-only too.
        var a = Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1)).Parse().Files[0];
        var b = Cobertura.NewDoc().AddClass("a.cs", c => c.Line(2, 1)).Parse().Files[0];

        var (merged, _) = a.MergeWith(b);

        var hits = (await Assert.That(merged.LineHits).IsAssignableTo<IDictionary<int, int>>())!;
        Assert.ThrowsExactly<NotSupportedException>(() => hits[9999] = 1);
        var branches = (await Assert.That(merged.BranchesByLine).IsAssignableTo<IDictionary<int, (int Covered, int Total)>>())!;
        Assert.ThrowsExactly<NotSupportedException>(() => branches[9999] = (1, 2));
    }
}