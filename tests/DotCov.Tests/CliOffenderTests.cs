using System.Text.Json;
using DotCov.Tests.Infrastructure;
using TUnit.Assertions.Enums;
using DotCov.Tool;

namespace DotCov.Tests;

/// <summary>
/// The offender list a failing gate prints: what to fix next. Files below the threshold of every
/// dimension that failed, worst first (lowest failing rate, ties by ordinal path), each with its
/// uncovered line ranges and partially covered branch lines, capped by --top (default 20).
/// Pinned through <see cref="DotCovCli.RunAsync"/> for check; the table form goes to stderr.
/// </summary>
public sealed class CliOffenderTests : IDisposable
{
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-cli-offenders-");

    public void Dispose() => _ws.Dispose();

    private static async Task<(int Code, string StdOut, string StdErr)> Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await DotCovCli.RunAsync(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    /// <summary>
    /// A: 4/7 lines, 1/6 branches, uncovered 21,23-24, partial branches 20 (1/2) and 26 (0/4).
    /// B: 1/2 lines, no branch data. C: 3/3 lines, 1/2 branches. Totals: 8/12 lines, 2/8 branches.
    /// </summary>
    private string ThreeFiles() =>
        _ws.Write("three.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/C.cs", c => c.Line(1, hits: 1).Line(2, hits: 1).Branch(3, "50% (1/2)"))
            .AddClass("src/A.cs", c => c
                .Line(19, hits: 1).Branch(20, "50% (1/2)").Line(21, hits: 0).Line(22, hits: 1)
                .Line(23, hits: 0).Line(24, hits: 0).Branch(26, "0% (0/4)"))
            .AddClass("src/B.cs", c => c.Line(1, hits: 1).Line(2, hits: 0)));

    private static string Lines(params string[] lines) => string.Join(Environment.NewLine, lines) + Environment.NewLine;

    [Test]
    public async Task BothDimensionsFail_ListsEveryFileBelowEither_WorstFirst_WithWhatToCover()
    {
        var (code, _, stderr) = await Run("check", ThreeFiles(), "--min-line", "80", "--min-branch", "60");

        await Assert.That(code).IsEqualTo(1);
        // A's worst rate is its 16.6% branch rate; B and C tie at 50% and sort by path.
        await Assert.That(stderr).StartsWith(Lines(
            "FAIL: line 66.6% (min 80%), branch 25.0% (min 60%) - line and branch coverage below threshold",
            "files below line or branch threshold:",
            "  src/A.cs  57.1% lines  16.6% branches",
            "    uncovered 21,23-24 · branches 20 (1/2), 26 (0/4)",
            "  src/B.cs  50.0% lines",
            "    uncovered 2",
            "  src/C.cs  100.0% lines  50.0% branches",
            "    branches 3 (1/2)"));
    }

    [Test]
    public async Task BranchOnlyFailure_ListsTheFilesBelowTheBranchThreshold()
    {
        // The line dimension is unchecked; B has no branch data and is not blamed.
        var (code, _, stderr) = await Run("check", ThreeFiles(), "--min-branch", "60");

        await Assert.That(code).IsEqualTo(1);
        await Assert.That(stderr).StartsWith(Lines(
            "FAIL: line 66.7% (min 0%), branch 25.0% (min 60%) - branch coverage below threshold",
            "files below branch threshold:",
            "  src/A.cs  57.1% lines  16.6% branches",
            "    uncovered 21,23-24 · branches 20 (1/2), 26 (0/4)",
            "  src/C.cs  100.0% lines  50.0% branches",
            "    branches 3 (1/2)"));
        await Assert.That(stderr).DoesNotContain("src/B.cs");
    }

    [Test]
    public async Task LineOnlyFailure_ListsOnlyTheFilesBelowTheLineThreshold()
    {
        // C is fully line-covered, so a line failure does not blame it.
        var (code, _, stderr) = await Run("check", ThreeFiles(), "--min-line", "80");

        await Assert.That(code).IsEqualTo(1);
        await Assert.That(stderr).StartsWith(Lines(
            "FAIL: line 66.6% (min 80%), branch 25.0% (min 0%) - line coverage below threshold",
            "files below line threshold:",
            "  src/B.cs  50.0% lines",
            "    uncovered 2",
            "  src/A.cs  57.1% lines  16.7% branches",
            "    uncovered 21,23-24 · branches 20 (1/2), 26 (0/4)"));
        await Assert.That(stderr).DoesNotContain("src/C.cs");
    }

    [Test]
    public async Task Top_CapsTheList_AndCountsTheRest()
    {
        var (_, _, stderr) = await Run("check", ThreeFiles(), "--min-line", "80", "--min-branch", "60", "--top", "1");

        await Assert.That(stderr).Contains(Lines(
            "  src/A.cs  57.1% lines  16.6% branches",
            "    uncovered 21,23-24 · branches 20 (1/2), 26 (0/4)",
            "  … 2 more files"));
        await Assert.That(stderr).DoesNotContain("src/B.cs");
    }

    [Test]
    public async Task DefaultCap_IsTwentyFiles()
    {
        var doc = Cobertura.NewDoc();
        for (var i = 10; i < 31; i++)
            doc.AddClass($"src/F{i}.cs", c => c.Line(1, hits: 0));
        var path = _ws.Write("many.cobertura.xml", doc);

        var (_, _, stderr) = await Run("check", path, "--min-line", "50");

        await Assert.That(stderr).Contains("  src/F29.cs  0.0% lines");
        await Assert.That(stderr).DoesNotContain("src/F30.cs");
        await Assert.That(stderr).Contains("  … 1 more files");
    }

    [Test]
    public async Task Json_Offenders_CarryRangesAndPartialBranches()
    {
        var (_, stdout, _) = await Run("check", ThreeFiles(), "--min-line", "80", "--min-branch", "60", "--top", "2", "--format", "json");

        var gate = JsonDocument.Parse(stdout).RootElement.GetProperty("gate");
        var offenders = gate.GetProperty("offenders").EnumerateArray().ToArray();
        await Assert.That(offenders.Select(static o => o.GetProperty("path").GetString()!)).IsEquivalentTo(["src/A.cs", "src/B.cs"], CollectionOrdering.Matching);
        await Assert.That(gate.GetProperty("moreOffenders").GetInt32()).IsEqualTo(1);

        var a = offenders[0];
        await Assert.That(a.EnumerateObject().Select(static p => p.Name))
            .IsEquivalentTo(["path", "lineRate", "branchRate", "uncovered", "partialBranches"], CollectionOrdering.Matching);
        // Rates below a failed minimum floor, so 57.142…% never rounds up.
        await Assert.That(a.GetProperty("lineRate").GetDouble()).IsEqualTo(57.14);
        await Assert.That(a.GetProperty("branchRate").GetDouble()).IsEqualTo(16.66);
        await Assert.That(a.GetProperty("uncovered").GetString()).IsEqualTo("21,23-24");
        var partial = a.GetProperty("partialBranches").EnumerateArray()
            .Select(static b => (b.GetProperty("line").GetInt32(), b.GetProperty("covered").GetInt32(), b.GetProperty("total").GetInt32()));
        await Assert.That(partial).IsEquivalentTo([(20, 1, 2), (26, 0, 4)], CollectionOrdering.Matching);

        var b = offenders[1];
        await Assert.That(b.GetProperty("branchRate").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(b.GetProperty("partialBranches").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task Json_PassingDimension_KeepsNormalRounding_InOffenders()
    {
        // Lines fail; A's branch rate is not judged, so 16.666…% rounds to 16.67.
        var (_, stdout, _) = await Run("check", ThreeFiles(), "--min-line", "80", "--format", "json");

        var offenders = JsonDocument.Parse(stdout).RootElement.GetProperty("gate").GetProperty("offenders");
        await Assert.That(offenders[1].GetProperty("branchRate").GetDouble()).IsEqualTo(16.67);
    }

    [Test]
    public async Task NoData_ListsNoOffenders_EvenWithALineRateBelowItsMinimum()
    {
        // The branch threshold makes the gate NODATA; nothing was judged, so nothing is blamed.
        var (code, stdout, stderr) = await Run("check", _ws.Write("lines.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/B.cs", c => c.Line(1, hits: 1).Line(2, hits: 0))), "--min-line", "80", "--min-branch", "50", "--format", "json");

        await Assert.That(code).IsEqualTo(3);
        await Assert.That(stderr).DoesNotContain("files below");
        await Assert.That(JsonDocument.Parse(stdout).RootElement.GetProperty("gate").GetProperty("offenders").GetArrayLength()).IsEqualTo(0);
    }
}
