using DotCov.Formatters;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;
/// <summary>
/// The movement threshold at rates other than zero, and the diff's file pairing when the
/// leftover files on either side carry no shared name.
/// </summary>
[TestClass]
public sealed class CoverageDiffEdgeTests
{
    [TestMethod]
    [DataRow(5000, 5001)]
    [DataRow(9999, 10000)]
    [DataRow(1, 2)]
    public void Compare_RiseOfExactlyMovementEpsilon_IsAnImprovementFromAnyBaseRate(int beforeHit, int afterHit)
    {
        // One more covered line out of 10,000 moves the rate by exactly MovementEpsilon, which
        // counts as movement (FileDelta.Compared: "differ by at least MovementEpsilon"). From
        // 5000/10000 a raw subtraction lands one rounding error short (9.999999999998899E-05),
        // as does 99.99% → 100%; both must still read as Modified.
        var result = CoverageDiff.Compare(Reports.Single("a.cs", hit: beforeHit, total: 10000), Reports.Single("a.cs", hit: afterHit, total: 10000));
        var d = Assert.ContainsSingle(result.Files);
        Assert.AreEqual(FileChangeKind.Modified, d.Change);
        Assert.AreEqual(CoverageDiff.MovementEpsilon, d.Delta);
        Assert.IsTrue(d.IsImprovement);
        Assert.AreEqual("a.cs", result.Improvements.Single().Path);
        Assert.IsEmpty(result.Regressions);
        Assert.AreEqual(CoverageDiff.MovementEpsilon, result.Delta);
        // The rendered colour reads the same value: movement, green — row and TOTAL alike.
        var pen = new AnsiPen(enabled: true);
        Assert.StartsWith("\e[32m", pen.Delta("x", d.Delta));
        Assert.StartsWith("\e[32m", pen.Delta("x", result.Delta));
    }

    [TestMethod]
    [DataRow(5001, 5000)]
    [DataRow(10000, 9999)]
    public void Compare_FallOfExactlyMovementEpsilon_IsARegressionFromAnyBaseRate(int beforeHit, int afterHit)
    {
        var result = CoverageDiff.Compare(Reports.Single("a.cs", hit: beforeHit, total: 10000), Reports.Single("a.cs", hit: afterHit, total: 10000));
        var d = Assert.ContainsSingle(result.Files);
        Assert.AreEqual(FileChangeKind.Modified, d.Change);
        Assert.AreEqual(-CoverageDiff.MovementEpsilon, d.Delta);
        Assert.IsTrue(d.IsRegression);
        Assert.AreEqual("a.cs", result.Regressions.Single().Path);
        Assert.AreEqual(-CoverageDiff.MovementEpsilon, result.Delta);
        Assert.StartsWith("\e[31m", new AnsiPen(enabled: true).Delta("x", d.Delta));
    }

    [TestMethod]
    public void Compare_SubEpsilonMovementAtAHighBaseRate_StaysUnchanged()
    {
        // Absorbing rounding error must not widen the threshold: half an epsilon is noise.
        var result = CoverageDiff.Compare(Reports.Single("a.cs", hit: 19999, total: 20000), Reports.Single("a.cs", hit: 20000, total: 20000));
        var d = Assert.ContainsSingle(result.Files);
        Assert.AreEqual(FileChangeKind.Unchanged, d.Change);
        Assert.AreEqual(CoverageDiff.MovementEpsilon / 2, d.Delta!.Value, 1e-15);
        Assert.IsEmpty(result.Improvements);
    }

    [TestMethod]
    public void Compare_EqualRates_HaveAPositiveZeroDelta()
    {
        // Equal rates give exactly +0.0 on the file and on the total, never -0.0.
        var result = CoverageDiff.Compare(Reports.Single("a.cs", hit: 1, total: 3), Reports.Single("a.cs", hit: 1, total: 3));
        var d = Assert.ContainsSingle(result.Files);
        Assert.AreEqual(FileChangeKind.Unchanged, d.Change);
        Assert.IsFalse(double.IsNegative(d.Delta!.Value));
        Assert.AreEqual(0.0, d.Delta.Value);
        Assert.IsFalse(double.IsNegative(result.Delta!.Value));
    }

    [TestMethod]
    public void Compare_LeftoverNameAmbiguousOnTheAfterSide_PairsNothing()
    {
        // One leftover Before file, two leftover After files with its name: no unique partner,
        // so nothing is guessed together even though the roots differ.
        var before = new CoverageReport([new FileCoverage("old/App.cs", 1, 2, 0, 0)])
        {
            SourceRoots = ["/old"]
        };
        var after = new CoverageReport([new FileCoverage("svc-a/App.cs", 2, 2, 0, 0), new FileCoverage("svc-b/App.cs", 0, 2, 0, 0)])
        {
            SourceRoots = ["/new"]
        };
        var result = CoverageDiff.Compare(before, after);
        Assert.AreEqual("old/App.cs", result.Removed.Single().Path);
        Assert.AreSequenceEqual(["svc-a/App.cs", "svc-b/App.cs"], result.Added.Select(static f => f.Path), SequenceOrder.InAnyOrder);
        Assert.IsFalse(result.Files.Any(static f => f.Change is FileChangeKind.Modified));
    }

    [TestMethod]
    public void Compare_OneSideUnmeasured_HasNoOverallDelta()
    {
        // A diff against a report without line data is not a comparison: no overall delta,
        // whichever side is empty.
        var measured = Reports.Single("a.cs", hit: 1, total: 2);
        Assert.IsNull(CoverageDiff.Compare(CoverageReport.Empty, measured).Delta);
        Assert.IsNull(CoverageDiff.Compare(measured, CoverageReport.Empty).Delta);
        Assert.AreEqual(0.0, CoverageDiff.Compare(measured, measured).Delta);
    }

    [TestMethod]
    public void Compared_NegativeRoundingNoise_SnapsToPositiveZero()
    {
        // One ulp of rate noise below the before rate is no movement at all: the delta is +0.0,
        // never -0.0 (which renders "-0.0%" and "-0" in JSON for an unchanged file).
        var d = FileDelta.Compared("a.cs", 0.5000000000000001, 0.5);
        var total = new CoverageDiffResult([d], 0.5000000000000001, 0.5);
        Assert.AreEqual(FileChangeKind.Unchanged, d.Change);
        Assert.IsFalse(double.IsNegative(d.Delta!.Value));
        Assert.AreEqual(0.0, d.Delta.Value);
        Assert.IsFalse(double.IsNegative(total.Delta!.Value));
    }

    [TestMethod]
    public void Compare_LeftoverFilesWithDifferentNames_StayRemovedAndAdded()
    {
        // Leftovers exist on both sides but share no file name, so the unique-name fallback has
        // nothing to pair: one Removed, one Added, nothing fused.
        var before = Reports.Single("src/Old.cs", hit: 1, total: 2);
        var after = Reports.Single("src/New.cs", hit: 2, total: 2);
        var result = CoverageDiff.Compare(before, after);
        Assert.AreEqual(2, result.Files.Count);
        Assert.AreEqual("src/Old.cs", result.Removed.Single().Path);
        Assert.AreEqual("src/New.cs", result.Added.Single().Path);
        Assert.IsFalse(result.Files.Any(static f => f.Change is FileChangeKind.Modified));
    }
}
