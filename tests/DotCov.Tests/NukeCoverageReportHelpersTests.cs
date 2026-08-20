using DotCov.Nuke;
using DotCov.Tests.Infrastructure;
using Xunit;

namespace DotCov.Tests;

public sealed class NukeCoverageReportHelpersTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dotcov-nuke-helpers-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private void Write(string relative, Cobertura builder)
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, builder.ToBytes());
    }

    // ── LoadReport ────────────────────────────────────────────────────────────

    [Fact]
    public void LoadReport_MissingDirectory_ReturnsEmptySingleton() =>
        Assert.Same(CoverageReport.Empty,
            CoverageReportHelpers.LoadReport(Path.Combine(_root, "does-not-exist")));

    [Fact]
    public void LoadReport_EmptyDirectory_ReturnsEmptySingleton() =>
        Assert.Same(CoverageReport.Empty, CoverageReportHelpers.LoadReport(_root));

    [Fact]
    public void LoadReport_FileWithNoClasses_IsNotTheEmptySingleton()
    {
        // The target's hard-fail relies on this boundary: a discovered-but-dataless report
        // must reach the gate (NoData), not the "no files found" assert.
        Write("coverage.cobertura.xml", Cobertura.NewDoc());

        var report = CoverageReportHelpers.LoadReport(_root);

        Assert.NotSame(CoverageReport.Empty, report);
        Assert.Empty(report.Files);
    }

    [Fact]
    public void LoadReport_NestedFiles_MergesAll()
    {
        Write("test1/coverage.cobertura.xml",
            Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1).Line(2, 0)));
        Write("test2/coverage.cobertura.xml",
            Cobertura.NewDoc().AddClass("b.cs", c => c.Line(1, 1)));

        var report = CoverageReportHelpers.LoadReport(_root);

        Assert.Equal(2, report.Files.Count);
    }

    [Fact]
    public void LoadReport_MergeOrder_IsOrdinalByPath_NotCreationOrder()
    {
        // zeta is created first; the ordinal sort must still parse alpha first, pinning the
        // BranchTotalMismatch operand order regardless of filesystem enumeration order
        // (the raw GlobFiles pipeline this replaced was enumeration-order-dependent).
        Write("zeta/coverage.cobertura.xml",
            Cobertura.NewDoc().AddClass("a.cs", c => c.Branch(10, "50% (1/2)")));
        Write("alpha/coverage.cobertura.xml",
            Cobertura.NewDoc().AddClass("a.cs", c => c.Branch(10, "75% (3/4)")));

        var report = CoverageReportHelpers.LoadReport(_root);

        var warning = Assert.Single(report.Warnings);
        Assert.Equal(CoverageWarningKind.BranchTotalMismatch, warning.Kind);
        Assert.Equal("Total 4 vs 2 — keeping 4", warning.Detail);
    }

    // ── ParseThreshold ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("80", 80.0)]
    [InlineData("0", 0.0)]
    [InlineData("72.5", 72.5)]
    public void ParseThreshold_ValidNumber_Parses(string value, double expected) =>
        Assert.Equal(expected, CoverageReportHelpers.ParseThreshold(value, "Coverage MinLine"));

    [Theory]
    [InlineData("eighty")]
    [InlineData("80,5")] // invariant culture: comma is not a decimal separator
    [InlineData("")]
    public void ParseThreshold_Garbage_ThrowsNamingTheParameter(string value)
    {
        var ex = Assert.Throws<ArgumentException>(
            () => CoverageReportHelpers.ParseThreshold(value, "Coverage MinLine"));

        Assert.Contains("Coverage MinLine", ex.Message);
        Assert.Contains($"'{value}'", ex.Message);
    }

    // ── ParseFlag ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    public void ParseFlag_TrueOrFalse_Parses(string value, bool expected) =>
        Assert.Equal(expected, CoverageReportHelpers.ParseFlag(value, "Coverage ExcludeGeneratedParam"));

    [Theory]
    [InlineData("1")]
    [InlineData("yes")]
    [InlineData("on")]
    [InlineData("")]
    public void ParseFlag_TruthySpelling_FailsLoudlyInsteadOfSilentFalse(string value)
    {
        var ex = Assert.Throws<ArgumentException>(
            () => CoverageReportHelpers.ParseFlag(value, "Coverage ExcludeGeneratedParam"));

        Assert.Contains("Coverage ExcludeGeneratedParam", ex.Message);
        Assert.Contains($"'{value}'", ex.Message);
    }

    // ── ParseFormat ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("table", "table")]
    [InlineData("json", "json")]
    [InlineData("markdown", "markdown")]
    [InlineData("md", "markdown")] // alias canonicalizes
    public void ParseFormat_KnownFormat_ReturnsCanonicalName(string value, string expected) =>
        Assert.Equal(expected, CoverageReportHelpers.ParseFormat(value, "Coverage Format"));

    [Theory]
    [InlineData("markdwon")] // the typo the old silent-table fallback swallowed
    [InlineData("xml")]
    [InlineData("TABLE")] // case-sensitive, matching the original switch arms
    [InlineData("")]
    public void ParseFormat_UnknownFormat_FailsLoudlyInsteadOfSilentTable(string value)
    {
        var ex = Assert.Throws<ArgumentException>(
            () => CoverageReportHelpers.ParseFormat(value, "Coverage Format"));

        Assert.Contains("Coverage Format", ex.Message);
        Assert.Contains($"'{value}'", ex.Message);
    }

    // ── TryAppendGitHubStepSummary ────────────────────────────────────────────

    [Fact]
    public void TryAppendGitHubStepSummary_NullPath_ReturnsFalse() =>
        Assert.False(CoverageReportHelpers.TryAppendGitHubStepSummary(null, "# md"));

    [Fact]
    public void TryAppendGitHubStepSummary_EmptyPath_ReturnsFalse() =>
        Assert.False(CoverageReportHelpers.TryAppendGitHubStepSummary("", "# md"));

    [Fact]
    public void TryAppendGitHubStepSummary_WritablePath_Appends()
    {
        var path = Path.Combine(_root, "summary.md");

        Assert.True(CoverageReportHelpers.TryAppendGitHubStepSummary(path, "one"));
        Assert.True(CoverageReportHelpers.TryAppendGitHubStepSummary(path, "two"));

        Assert.Equal("onetwo", File.ReadAllText(path));
    }

    [Fact]
    public void TryAppendGitHubStepSummary_PathIsDirectory_ReturnsFalseWithoutThrowing() =>
        Assert.False(CoverageReportHelpers.TryAppendGitHubStepSummary(_root, "# md"));

    [Fact]
    public void TryAppendGitHubStepSummary_MissingParentDirectory_ReturnsFalseWithoutThrowing() =>
        Assert.False(CoverageReportHelpers.TryAppendGitHubStepSummary(
            Path.Combine(_root, "no-such-dir", "summary.md"), "# md"));
}
