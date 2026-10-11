using DotCov.Formatters;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

/// <summary>
/// The movement threshold at rates other than zero, and the diff's file pairing when the
/// leftover files on either side carry no shared name.
/// </summary>
public sealed class CoverageDiffEdgeTests
{
    [Test]
    [Arguments(5000, 5001)]
    [Arguments(9999, 10000)]
    [Arguments(1, 2)]
    public async Task Compare_RiseOfExactlyMovementEpsilon_IsAnImprovementFromAnyBaseRate(int beforeHit, int afterHit)
    {
        // One more covered line out of 10,000 moves the rate by exactly MovementEpsilon, which
        // counts as movement (FileDelta.Compared: "differ by at least MovementEpsilon"). From
        // 5000/10000 a raw subtraction lands one rounding error short (9.999999999998899E-05),
        // as does 99.99% → 100%; both must still read as Modified.
        var result = CoverageDiff.Compare(
            Reports.Single("a.cs", hit: beforeHit, total: 10000),
            Reports.Single("a.cs", hit: afterHit, total: 10000));

        var d = await Assert.That(result.Files).HasSingleItem();
        await Assert.That(d.Change).IsEqualTo(FileChangeKind.Modified);
        await Assert.That(d.Delta).IsEqualTo(CoverageDiff.MovementEpsilon);
        await Assert.That(d.IsImprovement).IsTrue();
        await Assert.That(result.Improvements.Single().Path).IsEqualTo("a.cs");
        await Assert.That(result.Regressions).IsEmpty();
        await Assert.That(result.Delta).IsEqualTo(CoverageDiff.MovementEpsilon);

        // The rendered colour reads the same value: movement, green — row and TOTAL alike.
        var pen = new AnsiPen(enabled: true);
        await Assert.That(pen.Delta("x", d.Delta)).StartsWith("\e[32m");
        await Assert.That(pen.Delta("x", result.Delta)).StartsWith("\e[32m");
    }

    [Test]
    [Arguments(5001, 5000)]
    [Arguments(10000, 9999)]
    public async Task Compare_FallOfExactlyMovementEpsilon_IsARegressionFromAnyBaseRate(int beforeHit, int afterHit)
    {
        var result = CoverageDiff.Compare(
            Reports.Single("a.cs", hit: beforeHit, total: 10000),
            Reports.Single("a.cs", hit: afterHit, total: 10000));

        var d = await Assert.That(result.Files).HasSingleItem();
        await Assert.That(d.Change).IsEqualTo(FileChangeKind.Modified);
        await Assert.That(d.Delta).IsEqualTo(-CoverageDiff.MovementEpsilon);
        await Assert.That(d.IsRegression).IsTrue();
        await Assert.That(result.Regressions.Single().Path).IsEqualTo("a.cs");
        await Assert.That(result.Delta).IsEqualTo(-CoverageDiff.MovementEpsilon);
        await Assert.That(new AnsiPen(enabled: true).Delta("x", d.Delta)).StartsWith("\e[31m");
    }

    [Test]
    public async Task Compare_SubEpsilonMovementAtAHighBaseRate_StaysUnchanged()
    {
        // Absorbing rounding error must not widen the threshold: half an epsilon is noise.
        var result = CoverageDiff.Compare(
            Reports.Single("a.cs", hit: 19999, total: 20000),
            Reports.Single("a.cs", hit: 20000, total: 20000));

        var d = await Assert.That(result.Files).HasSingleItem();
        await Assert.That(d.Change).IsEqualTo(FileChangeKind.Unchanged);
        await Assert.That(d.Delta!.Value).IsEqualTo(CoverageDiff.MovementEpsilon / 2).Within(1e-15);
        await Assert.That(result.Improvements).IsEmpty();
    }

    [Test]
    public async Task Compare_EqualRates_HaveAPositiveZeroDelta()
    {
        // Equal rates give exactly +0.0 on the file and on the total, never -0.0.
        var result = CoverageDiff.Compare(
            Reports.Single("a.cs", hit: 1, total: 3),
            Reports.Single("a.cs", hit: 1, total: 3));

        var d = await Assert.That(result.Files).HasSingleItem();
        await Assert.That(d.Change).IsEqualTo(FileChangeKind.Unchanged);
        await Assert.That(double.IsNegative(d.Delta!.Value)).IsFalse();
        await Assert.That(d.Delta.Value).IsEqualTo(0.0);
        await Assert.That(double.IsNegative(result.Delta!.Value)).IsFalse();
    }

    [Test]
    public async Task Compare_LeftoverNameAmbiguousOnTheAfterSide_PairsNothing()
    {
        // One leftover Before file, two leftover After files with its name: no unique partner,
        // so nothing is guessed together even though the roots differ.
        var before = new CoverageReport([new FileCoverage("old/App.cs", 1, 2, 0, 0)]) { SourceRoots = ["/old"] };
        var after = new CoverageReport([
            new FileCoverage("svc-a/App.cs", 2, 2, 0, 0),
            new FileCoverage("svc-b/App.cs", 0, 2, 0, 0)
        ]) { SourceRoots = ["/new"] };

        var result = CoverageDiff.Compare(before, after);

        await Assert.That(result.Removed.Single().Path).IsEqualTo("old/App.cs");
        await Assert.That(result.Added.Select(static f => f.Path)).IsEquivalentTo(["svc-a/App.cs", "svc-b/App.cs"]);
        await Assert.That(result.Files.Any(static f => f.Change is FileChangeKind.Modified)).IsFalse();
    }

    [Test]
    public async Task Compare_OneSideUnmeasured_HasNoOverallDelta()
    {
        // A diff against a report without line data is not a comparison: no overall delta,
        // whichever side is empty.
        var measured = Reports.Single("a.cs", hit: 1, total: 2);

        await Assert.That(CoverageDiff.Compare(CoverageReport.Empty, measured).Delta).IsNull();
        await Assert.That(CoverageDiff.Compare(measured, CoverageReport.Empty).Delta).IsNull();
        await Assert.That(CoverageDiff.Compare(measured, measured).Delta).IsEqualTo(0.0);
    }

    [Test]
    public async Task Compared_NegativeRoundingNoise_SnapsToPositiveZero()
    {
        // One ulp of rate noise below the before rate is no movement at all: the delta is +0.0,
        // never -0.0 (which renders "-0.0%" and "-0" in JSON for an unchanged file).
        var d = FileDelta.Compared("a.cs", 0.5000000000000001, 0.5);
        var total = new CoverageDiffResult([d], 0.5000000000000001, 0.5);

        await Assert.That(d.Change).IsEqualTo(FileChangeKind.Unchanged);
        await Assert.That(double.IsNegative(d.Delta!.Value)).IsFalse();
        await Assert.That(d.Delta.Value).IsEqualTo(0.0);
        await Assert.That(double.IsNegative(total.Delta!.Value)).IsFalse();
    }

    [Test]
    public async Task Compare_LeftoverFilesWithDifferentNames_StayRemovedAndAdded()
    {
        // Leftovers exist on both sides but share no file name, so the unique-name fallback has
        // nothing to pair: one Removed, one Added, nothing fused.
        var before = Reports.Single("src/Old.cs", hit: 1, total: 2);
        var after = Reports.Single("src/New.cs", hit: 2, total: 2);

        var result = CoverageDiff.Compare(before, after);

        await Assert.That(result.Files.Count).IsEqualTo(2);
        await Assert.That(result.Removed.Single().Path).IsEqualTo("src/Old.cs");
        await Assert.That(result.Added.Single().Path).IsEqualTo("src/New.cs");
        await Assert.That(result.Files.Any(static f => f.Change is FileChangeKind.Modified)).IsFalse();
    }
}
