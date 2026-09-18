using DotCov.Tests.Infrastructure;
using DotCov.Fallout;

namespace DotCov.Tests;

/// <summary>
/// The parameter grammar of <see cref="ICoverageReport"/>, validated once at the build
/// boundary by <see cref="CoverageParameters"/>: invariant numbers, digits-only cap, strict
/// booleans, case-sensitive formats with the <c>md</c> alias, and the pattern shapes. The
/// consuming-build tests in <see cref="FalloutBuildTests"/> prove the same grammar through a
/// real Fallout process.
/// </summary>
public sealed class CoverageParametersTests
{
    [Test]
    public async Task Parse_Defaults_MatchTheDocumentedTable()
    {
        var p = CoverageParameters.Parse(
            CoverageParameters.DefaultMinLine, CoverageParameters.DefaultMinBranch, CoverageParameters.DefaultFormat,
            CoverageParameters.DefaultExcludeGenerated, CoverageParameters.DefaultPattern, CoverageParameters.DefaultMaxChars);

        await Assert.That(p).IsEqualTo(new CoverageParameters(80, 0, CoverageFormat.Table, false, ReportPattern.Default, 50_000_000));
    }

    [Test]
    [Arguments("50000000", 50_000_000L)]
    [Arguments("0", 0L)] // 0 = no cap (XmlReaderSettings.MaxCharactersInDocument semantics)
    [Arguments("1024", 1024L)]
    public async Task ParseMaxChars_ValidValue_Parses(string value, long expected) =>
        await Assert.That(CoverageParameters.ParseMaxChars(value, "Coverage MaxCharsParam")).IsEqualTo(expected);

    [Test]
    [Arguments("-1")] // digits only, mirroring the CLI's --max-chars: a sign is invalid
    [Arguments("+1")]
    [Arguments("1_000")]
    [Arguments("ten")]
    [Arguments("")]
    public async Task ParseMaxChars_Garbage_ThrowsNamingTheParameter(string value)
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(
            () => CoverageParameters.ParseMaxChars(value, "Coverage MaxCharsParam"));

        await Assert.That(ex.Message).Contains("Coverage MaxCharsParam");
        await Assert.That(ex.Message).Contains($"'{value}'");
    }

    [Test]
    [Arguments("80", 80.0)]
    [Arguments("0", 0.0)]
    [Arguments("72.5", 72.5)]
    public async Task ParseThreshold_ValidNumber_Parses(string value, double expected) =>
        await Assert.That(CoverageParameters.ParseThreshold(value, "Coverage MinLine")).IsEqualTo(expected);

    [Test]
    [Arguments("eighty")]
    [Arguments("80,5")] // invariant culture: comma is not a decimal separator
    [Arguments("")]
    public async Task ParseThreshold_Garbage_ThrowsNamingTheParameter(string value)
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(
            () => CoverageParameters.ParseThreshold(value, "Coverage MinLine"));

        await Assert.That(ex.Message).Contains("Coverage MinLine");
        await Assert.That(ex.Message).Contains($"'{value}'");
    }

    [Test]
    [Arguments("true", true)]
    [Arguments("True", true)]
    [Arguments("false", false)]
    [Arguments("FALSE", false)]
    public async Task ParseFlag_TrueOrFalse_Parses(string value, bool expected) =>
        await Assert.That(CoverageParameters.ParseFlag(value, "Coverage ExcludeGeneratedParam")).IsEqualTo(expected);

    [Test]
    [Arguments("1")]
    [Arguments("yes")]
    [Arguments("on")]
    [Arguments("")]
    public async Task ParseFlag_TruthySpelling_FailsLoudlyInsteadOfSilentFalse(string value)
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(
            () => CoverageParameters.ParseFlag(value, "Coverage ExcludeGeneratedParam"));

        await Assert.That(ex.Message).Contains("Coverage ExcludeGeneratedParam");
        await Assert.That(ex.Message).Contains($"'{value}'");
    }

    [Test]
    [Arguments("table", "Table")]
    [Arguments("json", "Json")]
    [Arguments("markdown", "Markdown")]
    [Arguments("md", "Markdown")] // alias canonicalizes
    public async Task ParseFormat_KnownFormat_ReturnsCanonicalFormat(string value, string expected) =>
        await Assert.That(CoverageParameters.ParseFormat(value, "Coverage Format").ToString()).IsEqualTo(expected);

    [Test]
    [Arguments("markdwon")] // the typo the old silent-table fallback swallowed
    [Arguments("xml")]
    [Arguments("TABLE")] // case-sensitive, matching the original switch arms
    [Arguments("")]
    public async Task ParseFormat_UnknownFormat_FailsLoudlyInsteadOfSilentTable(string value)
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(
            () => CoverageParameters.ParseFormat(value, "Coverage Format"));

        await Assert.That(ex.Message).Contains("Coverage Format");
        await Assert.That(ex.Message).Contains($"'{value}'");
    }

    [Test]
    [Arguments("coverage.xml", false)]
    [Arguments("**/coverage.xml", true)]
    public async Task ParsePattern_SupportedShape_Parses(string value, bool recursive)
    {
        var pattern = CoverageParameters.ParsePattern(value, "Coverage Pattern");

        await Assert.That(pattern.Recursive).IsEqualTo(recursive);
        await Assert.That(pattern.FileName).IsEqualTo("coverage.xml");
    }

    [Test]
    [Arguments("cov/*.xml")]
    [Arguments("")]
    public async Task ParsePattern_UnsupportedShape_ThrowsNamingTheParameter(string value)
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(
            () => CoverageParameters.ParsePattern(value, "Coverage Pattern"));

        await Assert.That(ex.Message).Contains("Invalid Coverage Pattern");
        await Assert.That(ex.Message).Contains($"'{value}'");
    }

    [Test]
    public async Task Parse_NegativeMaxChars_IsBlamedOnMaxChars_NotThePattern()
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(() => CoverageParameters.Parse("80", "0", "table", "false", "coverage.cobertura.xml", "-1"));

        await Assert.That(ex.Message).Contains("Coverage MaxCharsParam");
        await Assert.That(ex.Message).DoesNotContain("Coverage Pattern");
    }
}

public sealed class GitHubStepSummaryTests : IDisposable
{
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-summary-");

    public void Dispose() => _ws.Dispose();

    [Test]
    public async Task TryAppend_NullOrEmptyPath_ReturnsFalse()
    {
        await Assert.That(GitHubStepSummary.TryAppend(null, "# md")).IsFalse();
        await Assert.That(GitHubStepSummary.TryAppend("", "# md")).IsFalse();
    }

    [Test]
    public async Task TryAppend_WritablePath_Appends()
    {
        var path = _ws.PathOf("summary.md");

        await Assert.That(GitHubStepSummary.TryAppend(path, "one")).IsTrue();
        await Assert.That(GitHubStepSummary.TryAppend(path, "two")).IsTrue();

        await Assert.That(await File.ReadAllTextAsync(path)).IsEqualTo("onetwo");
    }

    [Test]
    public async Task TryAppend_PathIsDirectory_ReturnsFalseWithoutThrowing() =>
        await Assert.That(GitHubStepSummary.TryAppend(_ws.Root, "# md")).IsFalse();

    [Test]
    public async Task TryAppend_MissingParentDirectory_ReturnsFalseWithoutThrowing() =>
        await Assert.That(GitHubStepSummary.TryAppend(_ws.PathOf("no-such-dir/summary.md"), "# md")).IsFalse();
}
