using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;
/// <summary>
/// API-longevity hardening of the published structs and collection surfaces:
/// a <c>default(FileCoverage)</c>/<c>default(FileDelta)</c> (array growth, dictionary lookup
/// miss, bare <c>default</c>) must behave as an empty value instead of violating its own
/// non-nullable annotations, and the parser's internal accumulator dictionaries must not
/// escape mutable behind the <c>IReadOnly*</c> surface.
/// </summary>
[TestClass]
public sealed class CoreApiHardeningTests
{
    [TestMethod]
    public void DefaultFileCoverage_BehavesAsEmptyFile_NoNullReferences()
    {
        // Property initializers never run for default instances, so new FileCoverage[1] must
        // still expose empty collections, not nulls that make GetLineStatus throw.
        var arr = new FileCoverage[1];
        var f = arr[0];
        Assert.IsEmpty(f.LineHits);
        Assert.IsEmpty(f.BranchesByLine);
        Assert.IsEmpty(f.ConditionsByLine);
        Assert.IsEmpty(f.BranchesByOrigin);
        Assert.IsEmpty(f.UncoveredLines);
        Assert.IsEmpty(f.PartialBranches);
        Assert.AreEqual(LineStatus.Miss, f.GetLineStatus(1));
        Assert.IsFalse(f.TryGetLineStatus(1, out var status));
        Assert.AreEqual(LineStatus.Miss, status);
        Assert.IsNull(f.LineRate);
    }

    [TestMethod]
    public void DefaultFileCoverage_MergesLikeAnEmptyFile()
    {
        var measured = Reports.ClassifiedFile("a.cs", 1, 2, 0, 0, lineHits: new Dictionary<int, int> { [1] = 3, [2] = 0 }, branchesByLine: new Dictionary<int, (int Covered, int Total)>());
        var(merged, warnings) = default(FileCoverage).MergeWith(measured);
        Assert.IsEmpty(warnings);
        Assert.AreEqual(2, merged.LinesTotal);
        Assert.AreEqual(1, merged.LinesHit);
    }

    [TestMethod]
    public void DefaultCrapMethod_HasNoUncoveredLines()
    {
        var arr = new CrapMethod[1];
        Assert.IsEmpty(arr[0].UncoveredLines);
    }

    [TestMethod]
    public void DefaultFileDelta_HasEmptyLineChanges()
    {
        var arr = new FileDelta[1];
        Assert.IsEmpty(arr[0].LineChanges);
    }

    [TestMethod]
    public void ParsedReport_CollectionSurfaces_AreNotTheMutableAccumulators()
    {
        // Handing the LineAccumulator's live Dictionary instances straight to the report would
        // let a downcast mutation desynchronize LineHits from the precomputed
        // LinesHit/StrictlyHitLines aggregates, so the construction seam must wrap them read-only.
        var file = Cobertura.NewDoc().AddClass("src/A.cs", c => c.Line(1, hits: 1).Branch(10, "50% (1/2)")).Parse().Files[0];
        Assert.IsNotExactInstanceOfType<Dictionary<int, int>>(file.LineHits);
        Assert.IsNotExactInstanceOfType<Dictionary<int, (int Covered, int Total)>>(file.BranchesByLine);
        var hits = (Assert.IsInstanceOfType<IDictionary<int, int>>(file.LineHits));
        Assert.ThrowsExactly<NotSupportedException>(() => hits[9999] = 1);
    }

    [TestMethod]
    public void MergedFile_CollectionSurfaces_AreNotMutable()
    {
        // Same seam on the merge path: MergeWith builds fresh dicts, and they must leave the
        // method read-only too.
        var a = Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1)).Parse().Files[0];
        var b = Cobertura.NewDoc().AddClass("a.cs", c => c.Line(2, 1)).Parse().Files[0];
        var(merged, _) = a.MergeWith(b);
        var hits = (Assert.IsInstanceOfType<IDictionary<int, int>>(merged.LineHits));
        Assert.ThrowsExactly<NotSupportedException>(() => hits[9999] = 1);
        var branches = (Assert.IsInstanceOfType<IDictionary<int, (int Covered, int Total)>>(merged.BranchesByLine));
        Assert.ThrowsExactly<NotSupportedException>(() => branches[9999] = (1, 2));
    }
}
