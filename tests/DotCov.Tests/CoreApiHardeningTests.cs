using DotCov.Tests.Infrastructure;
using Xunit;

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
    [Fact]
    public void DefaultFileCoverage_BehavesAsEmptyFile_NoNullReferences()
    {
        // Property initializers never run for default instances — new FileCoverage[1] used to
        // hold null LineHits/UncoveredLines and GetLineStatus threw NullReferenceException.
        var arr = new FileCoverage[1];
        var f = arr[0];

        Assert.Empty(f.LineHits);
        Assert.Empty(f.BranchesByLine);
        Assert.Empty(f.ConditionsByLine);
        Assert.Empty(f.UncoveredLines);
        Assert.Empty(f.PartialBranches);
        Assert.Equal(LineStatus.Miss, f.GetLineStatus(1));
        Assert.False(f.TryGetLineStatus(1, out var status));
        Assert.Equal(LineStatus.Miss, status);
        Assert.Null(f.LineRate);
    }

    [Fact]
    public void DefaultFileCoverage_MergesLikeAnEmptyFile()
    {
        var measured = Reports.ClassifiedFile("a.cs", 1, 2, 0, 0,
            lineHits: new Dictionary<int, int> { [1] = 3, [2] = 0 },
            branchesByLine: new Dictionary<int, (int Covered, int Total)>());

        var (merged, warnings) = default(FileCoverage).MergeWith(measured);

        Assert.Empty(warnings);
        Assert.Equal(2, merged.LinesTotal);
        Assert.Equal(1, merged.LinesHit);
    }

    [Fact]
    public void DefaultFileDelta_HasEmptyLineChanges()
    {
        var arr = new FileDelta[1];

        Assert.Empty(arr[0].LineChanges);
    }

    [Fact]
    public void ParsedReport_CollectionSurfaces_AreNotTheMutableAccumulators()
    {
        // Materialize used to hand the LineAccumulator's live Dictionary instances straight to
        // the report: a downcast mutation desynchronized LineHits from the precomputed
        // LinesHit/StrictlyHitLines aggregates. The construction seam must wrap read-only.
        var file = Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c.Line(1, hits: 1).Branch(10, "50% (1/2)"))
            .Parse().Files[0];

        Assert.IsNotType<Dictionary<int, int>>(file.LineHits);
        Assert.IsNotType<Dictionary<int, (int Covered, int Total)>>(file.BranchesByLine);
        var hits = Assert.IsAssignableFrom<IDictionary<int, int>>(file.LineHits);
        Assert.Throws<NotSupportedException>(() => hits[9999] = 1);
    }

    [Fact]
    public void MergedFile_CollectionSurfaces_AreNotMutable()
    {
        // Same seam on the merge path: MergeWith builds fresh dicts, and they must leave the
        // method read-only too.
        var a = Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1)).Parse().Files[0];
        var b = Cobertura.NewDoc().AddClass("a.cs", c => c.Line(2, 1)).Parse().Files[0];

        var (merged, _) = a.MergeWith(b);

        var hits = Assert.IsAssignableFrom<IDictionary<int, int>>(merged.LineHits);
        Assert.Throws<NotSupportedException>(() => hits[9999] = 1);
        var branches = Assert.IsAssignableFrom<IDictionary<int, (int Covered, int Total)>>(merged.BranchesByLine);
        Assert.Throws<NotSupportedException>(() => branches[9999] = (1, 2));
    }
}
