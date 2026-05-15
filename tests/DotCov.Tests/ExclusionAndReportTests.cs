using DotCov.Tests.Infrastructure;
using Xunit;

namespace DotCov.Tests;

public sealed class ExclusionAndReportTests
{
    [Fact]
    public void WellKnown_ContainsExpectedPatterns()
    {
        Assert.Contains(".g.cs", ExclusionRules.WellKnown);
        Assert.Contains(".designer.cs", ExclusionRules.WellKnown);
        Assert.Contains("/obj/", ExclusionRules.WellKnown);
        Assert.Contains("/bin/", ExclusionRules.WellKnown);
        Assert.Contains("/Migrations/", ExclusionRules.WellKnown);
        Assert.Contains("d__", ExclusionRules.WellKnown);
        Assert.Contains("GlobalUsings", ExclusionRules.WellKnown);
        Assert.Contains("/Program.cs", ExclusionRules.WellKnown);
    }

    [Fact]
    public void Exclude_WithKeep_RestoresFilesMatchingKeepPattern()
    {
        // `Program.cs` is in WellKnown, but a CLI tool whose entire surface lives there
        // can opt back in with `--keep Program.cs`.
        var report = new CoverageReport([
            new FileCoverage("src/MyApp/Program.cs", 10, 12, 0, 0),
            new FileCoverage("src/MyApp/Service.cs", 8, 10, 0, 0),
            new FileCoverage("src/MyApp/obj/Generated.cs", 1, 1, 0, 0)
        ]);

        var filtered = report.Exclude(ExclusionRules.WellKnown, keep: ["Program.cs"]);

        Assert.Contains(filtered.Files, f => f.Path.EndsWith("Program.cs"));    // exempted
        Assert.Contains(filtered.Files, f => f.Path.EndsWith("Service.cs"));    // never excluded
        Assert.DoesNotContain(filtered.Files, f => f.Path.Contains("/obj/"));   // still excluded
    }

    [Fact]
    public void Exclude_KeepNotMatched_LeavesExclusionInPlace()
    {
        var report = new CoverageReport([
            new FileCoverage("src/MyApp/Program.cs", 10, 12, 0, 0)
        ]);

        var filtered = report.Exclude(ExclusionRules.WellKnown, keep: ["Worker.cs"]);

        Assert.Empty(filtered.Files);
    }

    [Fact]
    public void Exclude_EmptyPatterns_ReturnsSameInstance()
    {
        var report = Reports.Mixed;

        var filtered = report.Exclude([]);

        Assert.Same(report, filtered);
    }

    [Theory]
    [InlineData("src/MyApp/obj/Debug/file.cs", true)]
    [InlineData("src/MyApp/OBJ/Debug/file.cs", true)] // case-insensitive
    [InlineData("src/MyApp/bin/Release/file.cs", true)]
    [InlineData("Source/file.cs", false)]
    [InlineData("src/Foo.g.cs", true)]
    [InlineData("src/Form.Designer.cs", true)]
    [InlineData("src/MyApp/Migrations/20230101_Init.cs", true)]
    [InlineData("src/MyApp/<MyMethod>d__0.cs", true)]
    [InlineData("src/GlobalUsings.cs", true)]
    [InlineData("src/MyService.cs", false)]
    public void Exclude_WellKnown_FiltersByPattern(string path, bool shouldBeExcluded)
    {
        var report = new CoverageReport([
            new FileCoverage(path, 1, 1, 0, 0),
            new FileCoverage("KeepThis.cs", 1, 1, 0, 0)
        ]);

        var filtered = report.Exclude(ExclusionRules.WellKnown);

        if (shouldBeExcluded)
            Assert.DoesNotContain(filtered.Files, f => f.Path == path);
        else
            Assert.Contains(filtered.Files, f => f.Path == path);
    }

    [Fact]
    public void Exclude_DoesNotMutateSource()
    {
        var report = Reports.Mixed;
        var originalCount = report.Files.Count;

        _ = report.Exclude(["Unused"]);

        Assert.Equal(originalCount, report.Files.Count);
    }

    [Fact]
    public void Empty_StaticInstance_HasNoFilesAndPerfectRates()
    {
        Assert.Empty(CoverageReport.Empty.Files);
        Assert.Equal(1.0, CoverageReport.Empty.LineRate);
        Assert.Equal(1.0, CoverageReport.Empty.BranchRate);
        Assert.False(CoverageReport.Empty.HasBranchData);
    }

    [Fact]
    public void HasBranchData_FileWithBranches_True()
    {
        var file = new FileCoverage("a.cs", 1, 2, 1, 2);
        Assert.True(file.HasBranchData);
    }

    [Fact]
    public void HasBranchData_FileWithoutBranches_False()
    {
        var file = new FileCoverage("a.cs", 1, 2, 0, 0);
        Assert.False(file.HasBranchData);
    }

    [Fact]
    public void HasBranchData_ReportWithoutAnyBranches_False()
    {
        Assert.False(Reports.LinesOnly.HasBranchData);
        Assert.True(Reports.Mixed.HasBranchData);
    }

    [Fact]
    public void FileCoverage_MergeWith_UnionsLinesAndAppendsPartialBranches()
    {
        // `a` covers line 5, misses 10, has a partial branch on line 15 (1/2).
        // `b` covers lines 20 and 30, misses 25, has a partial branch on line 25 (0/2).
        // Merging keeps the highest hit count per line and derives PartialBranches from the
        // unioned BranchesByLine dict (disjoint partial-branch lines → both surface).
        var a = new FileCoverage("a.cs", LinesHit: 1, LinesTotal: 2, BranchesHit: 1, BranchesTotal: 2)
        {
            LineHits = new Dictionary<int, int> { [5] = 3, [10] = 0 },
            BranchesByLine = new Dictionary<int, (int Covered, int Total)> { [15] = (1, 2) }
        };
        var b = new FileCoverage("a.cs", LinesHit: 2, LinesTotal: 3, BranchesHit: 0, BranchesTotal: 2)
        {
            LineHits = new Dictionary<int, int> { [20] = 1, [25] = 0, [30] = 1 },
            BranchesByLine = new Dictionary<int, (int Covered, int Total)> { [25] = (0, 2) }
        };

        var merged = a.MergeWith(b);

        Assert.Equal([10, 25], merged.UncoveredLines);
        Assert.Equal(2, merged.PartialBranches.Count);
        Assert.Equal(3, merged.LinesHit);      // 5, 20, 30
        Assert.Equal(5, merged.LinesTotal);    // 5, 10, 20, 25, 30
        Assert.Equal(1, merged.BranchesHit);   // 1 (line 15) + 0 (line 25)
        Assert.Equal(4, merged.BranchesTotal); // 2 + 2
    }

    [Fact]
    public void FileCoverage_MergeWith_BranchesOnlyInOther_PreservesEntry()
    {
        // `a` has the same line tracked but no branch data for it; `b` carries the branches.
        // The merge must keep `b`'s BranchesByLine entry — a regression that overwrote with
        // the empty dict would silently flip the merged line from Partial back to Hit.
        var a = new FileCoverage("a.cs", 1, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 1 }
        };
        var b = new FileCoverage("a.cs", 1, 1, 1, 2)
        {
            LineHits = new Dictionary<int, int> { [10] = 1 },
            BranchesByLine = new Dictionary<int, (int Covered, int Total)> { [10] = (1, 2) }
        };

        var merged = a.MergeWith(b);

        Assert.Equal((1, 2), merged.BranchesByLine[10]);
        Assert.Equal(LineStatus.Partial, merged.GetLineStatus(10));
    }

    // ── Codecov-style strict line classification (Hit / Partial / Miss) ──

    [Fact]
    public void GetLineStatus_FullyCoveredLineWithoutBranches_IsHit()
    {
        var f = new FileCoverage("a.cs", 1, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 3 }
        };

        Assert.Equal(LineStatus.Hit, f.GetLineStatus(10));
    }

    [Fact]
    public void GetLineStatus_FullyCoveredLineWithAllBranchesExercised_IsHit()
    {
        var f = new FileCoverage("a.cs", 1, 1, 2, 2)
        {
            LineHits = new Dictionary<int, int> { [10] = 3 },
            BranchesByLine = new Dictionary<int, (int Covered, int Total)> { [10] = (2, 2) }
        };

        Assert.Equal(LineStatus.Hit, f.GetLineStatus(10));
    }

    [Fact]
    public void GetLineStatus_ExecutedLineWithIncompleteBranches_IsPartial()
    {
        var f = new FileCoverage("a.cs", 1, 1, 1, 2)
        {
            LineHits = new Dictionary<int, int> { [10] = 3 },
            BranchesByLine = new Dictionary<int, (int Covered, int Total)> { [10] = (1, 2) }
        };

        Assert.Equal(LineStatus.Partial, f.GetLineStatus(10));
    }

    [Fact]
    public void GetLineStatus_ZeroHits_IsMissEvenWithBranchData()
    {
        var f = new FileCoverage("a.cs", 0, 1, 0, 2)
        {
            LineHits = new Dictionary<int, int> { [10] = 0 },
            BranchesByLine = new Dictionary<int, (int Covered, int Total)> { [10] = (0, 2) }
        };

        Assert.Equal(LineStatus.Miss, f.GetLineStatus(10));
    }

    [Fact]
    public void GetLineStatus_UnknownLine_IsMiss()
    {
        var f = new FileCoverage("a.cs", 0, 0, 0, 0);

        Assert.Equal(LineStatus.Miss, f.GetLineStatus(999));
    }

    [Fact]
    public void TryGetLineStatus_TrackedHitLine_ReturnsTrueAndHit()
    {
        var f = new FileCoverage("a.cs", 1, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 3 }
        };

        Assert.True(f.TryGetLineStatus(10, out var status));
        Assert.Equal(LineStatus.Hit, status);
    }

    [Fact]
    public void TryGetLineStatus_TrackedZeroHitLine_ReturnsTrueAndMiss()
    {
        // The key contract: line IS tracked but had zero hits. Distinct from untracked.
        var f = new FileCoverage("a.cs", 0, 1, 0, 0)
        {
            LineHits = new Dictionary<int, int> { [10] = 0 }
        };

        Assert.True(f.TryGetLineStatus(10, out var status));
        Assert.Equal(LineStatus.Miss, status);
    }

    [Fact]
    public void TryGetLineStatus_TrackedPartialBranchLine_ReturnsTrueAndPartial()
    {
        var f = new FileCoverage("a.cs", 1, 1, 1, 2)
        {
            LineHits = new Dictionary<int, int> { [10] = 3 },
            BranchesByLine = new Dictionary<int, (int Covered, int Total)> { [10] = (1, 2) }
        };

        Assert.True(f.TryGetLineStatus(10, out var status));
        Assert.Equal(LineStatus.Partial, status);
    }

    [Fact]
    public void TryGetLineStatus_UnknownLine_ReturnsFalseAndMiss()
    {
        // The other key contract: false signals "not tracked", out param is Miss for ergonomics.
        var f = new FileCoverage("a.cs", 0, 0, 0, 0);

        Assert.False(f.TryGetLineStatus(999, out var status));
        Assert.Equal(LineStatus.Miss, status);
    }

    [Fact]
    public void StrictLineRate_DowngradesPartialBranches()
    {
        // 3 lines tracked: line 1 fully hit (no branches), line 2 hit but with partial branches,
        // line 3 missed. Standard LineRate = 2/3 ≈ 66.7% (lines 1 and 2 are "hit").
        // Strict LineRate = 1/3 ≈ 33.3% (only line 1 is fully Hit; line 2 is Partial, line 3 is Miss).
        var f = new FileCoverage("a.cs", 2, 3, 1, 2)
        {
            LineHits = new Dictionary<int, int> { [1] = 5, [2] = 3, [3] = 0 },
            BranchesByLine = new Dictionary<int, (int Covered, int Total)> { [2] = (1, 2) }
        };

        Assert.Equal(2.0 / 3.0, f.LineRate, 4);
        Assert.Equal(1.0 / 3.0, f.StrictLineRate, 4);
        Assert.Equal(1, f.StrictlyHitLines);
        Assert.Equal(1, f.PartiallyHitLines);
    }

    [Fact]
    public void StrictLineRate_AllLinesPartial_IsZero()
    {
        // Boundary: every tracked line has unfinished branches → StrictLineRate must be 0,
        // even though LineRate stays at 1.0 (every line was executed).
        var f = new FileCoverage("a.cs", 2, 2, 2, 4)
        {
            LineHits = new Dictionary<int, int> { [10] = 1, [20] = 1 },
            BranchesByLine = new Dictionary<int, (int Covered, int Total)>
            {
                [10] = (1, 2),
                [20] = (1, 2)
            }
        };

        Assert.Equal(1.0, f.LineRate);
        Assert.Equal(0.0, f.StrictLineRate);
        Assert.Equal(0, f.StrictlyHitLines);
        Assert.Equal(2, f.PartiallyHitLines);
    }

    [Fact]
    public void StrictLineRate_EmptyReport_ReturnsOne()
    {
        Assert.Equal(1.0, CoverageReport.Empty.StrictLineRate);
    }

    [Fact]
    public void FileCoverage_StrictLineRate_EmptyLines_ReturnsOne()
    {
        // Empty FileCoverage (no LineHits at all) — strict rate is vacuously 1.0 per the
        // same convention as LineRate, so callers don't have to special-case empty files.
        var f = new FileCoverage("a.cs", 0, 0, 0, 0);

        Assert.Equal(1.0, f.StrictLineRate);
    }

    [Fact]
    public void CoverageReport_StrictLineRate_SumsAcrossFiles()
    {
        // File A: 1 strict-hit + 1 partial + 1 miss → 1/3 strict.
        // File B: 2 strict-hits → 2/2 strict.
        // Combined: 3 strict-hits across 5 total lines → 60%.
        var report = new CoverageReport([
            new FileCoverage("a.cs", 2, 3, 1, 2)
            {
                LineHits = new Dictionary<int, int> { [1] = 1, [2] = 1, [3] = 0 },
                BranchesByLine = new Dictionary<int, (int Covered, int Total)> { [2] = (1, 2) }
            },
            new FileCoverage("b.cs", 2, 2, 0, 0)
            {
                LineHits = new Dictionary<int, int> { [10] = 1, [20] = 1 }
            }
        ]);

        Assert.Equal(0.6, report.StrictLineRate, 4);
    }

    [Fact]
    public void CoverageDiffResult_Improvements_FilterPositiveDeltas()
    {
        var diff = CoverageDiff.Compare(
            new CoverageReport([
                new FileCoverage("up.cs", 5, 10, 0, 0),
                new FileCoverage("down.cs", 9, 10, 0, 0)
            ]),
            new CoverageReport([
                new FileCoverage("up.cs", 9, 10, 0, 0),
                new FileCoverage("down.cs", 5, 10, 0, 0)
            ]));

        Assert.Single(diff.Improvements);
        Assert.Single(diff.Regressions);
        Assert.Equal("up.cs", diff.Improvements.Single().Path);
    }
}
