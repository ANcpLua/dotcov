using DotCov.Nuke;
using DotCov.Tests.Infrastructure;

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

    // ── ParseMaxChars ─────────────────────────────────────────────────────────

    [Test]
    [Arguments("50000000", 50_000_000L)]
    [Arguments("0", 0L)] // 0 = no cap (XmlReaderSettings.MaxCharactersInDocument semantics)
    [Arguments("1024", 1024L)]
    public async Task ParseMaxChars_ValidValue_Parses(string value, long expected) =>
        await Assert.That(CoverageReportHelpers.ParseMaxChars(value, "Coverage MaxCharsParam")).IsEqualTo(expected);

    [Test]
    [Arguments("-1")] // digits only, mirroring the CLI's --max-chars: a sign is invalid
    [Arguments("+1")]
    [Arguments("1_000")]
    [Arguments("ten")]
    [Arguments("")]
    public async Task ParseMaxChars_Garbage_ThrowsNamingTheParameter(string value)
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(
            () => CoverageReportHelpers.ParseMaxChars(value, "Coverage MaxCharsParam"));

        await Assert.That(ex.Message).Contains("Coverage MaxCharsParam");
        await Assert.That(ex.Message).Contains($"'{value}'");
    }

    // ── ParseThreshold ────────────────────────────────────────────────────────

    [Test]
    [Arguments("80", 80.0)]
    [Arguments("0", 0.0)]
    [Arguments("72.5", 72.5)]
    public async Task ParseThreshold_ValidNumber_Parses(string value, double expected) =>
        await Assert.That(CoverageReportHelpers.ParseThreshold(value, "Coverage MinLine")).IsEqualTo(expected);

    [Test]
    [Arguments("eighty")]
    [Arguments("80,5")] // invariant culture: comma is not a decimal separator
    [Arguments("")]
    public async Task ParseThreshold_Garbage_ThrowsNamingTheParameter(string value)
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(
            () => CoverageReportHelpers.ParseThreshold(value, "Coverage MinLine"));

        await Assert.That(ex.Message).Contains("Coverage MinLine");
        await Assert.That(ex.Message).Contains($"'{value}'");
    }

    // ── ParseFlag ─────────────────────────────────────────────────────────────

    [Test]
    [Arguments("true", true)]
    [Arguments("True", true)]
    [Arguments("false", false)]
    [Arguments("FALSE", false)]
    public async Task ParseFlag_TrueOrFalse_Parses(string value, bool expected) =>
        await Assert.That(CoverageReportHelpers.ParseFlag(value, "Coverage ExcludeGeneratedParam")).IsEqualTo(expected);

    [Test]
    [Arguments("1")]
    [Arguments("yes")]
    [Arguments("on")]
    [Arguments("")]
    public async Task ParseFlag_TruthySpelling_FailsLoudlyInsteadOfSilentFalse(string value)
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(
            () => CoverageReportHelpers.ParseFlag(value, "Coverage ExcludeGeneratedParam"));

        await Assert.That(ex.Message).Contains("Coverage ExcludeGeneratedParam");
        await Assert.That(ex.Message).Contains($"'{value}'");
    }

    // ── ParseFormat ───────────────────────────────────────────────────────────

    [Test]
    [Arguments("table", "table")]
    [Arguments("json", "json")]
    [Arguments("markdown", "markdown")]
    [Arguments("md", "markdown")] // alias canonicalizes
    public async Task ParseFormat_KnownFormat_ReturnsCanonicalName(string value, string expected) =>
        await Assert.That(CoverageReportHelpers.ParseFormat(value, "Coverage Format")).IsEqualTo(expected);

    [Test]
    [Arguments("markdwon")] // the typo the old silent-table fallback swallowed
    [Arguments("xml")]
    [Arguments("TABLE")] // case-sensitive, matching the original switch arms
    [Arguments("")]
    public async Task ParseFormat_UnknownFormat_FailsLoudlyInsteadOfSilentTable(string value)
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(
            () => CoverageReportHelpers.ParseFormat(value, "Coverage Format"));

        await Assert.That(ex.Message).Contains("Coverage Format");
        await Assert.That(ex.Message).Contains($"'{value}'");
    }

    // ── TryAppendGitHubStepSummary ────────────────────────────────────────────

    [Test]
    public async Task TryAppendGitHubStepSummary_NullPath_ReturnsFalse() =>
        await Assert.That(CoverageReportHelpers.TryAppendGitHubStepSummary(null, "# md")).IsFalse();

    [Test]
    public async Task TryAppendGitHubStepSummary_EmptyPath_ReturnsFalse() =>
        await Assert.That(CoverageReportHelpers.TryAppendGitHubStepSummary("", "# md")).IsFalse();

    [Test]
    public async Task TryAppendGitHubStepSummary_WritablePath_Appends()
    {
        var path = Path.Combine(_root, "summary.md");

        await Assert.That(CoverageReportHelpers.TryAppendGitHubStepSummary(path, "one")).IsTrue();
        await Assert.That(CoverageReportHelpers.TryAppendGitHubStepSummary(path, "two")).IsTrue();

        await Assert.That(File.ReadAllText(path)).IsEqualTo("onetwo");
    }

    [Test]
    public async Task TryAppendGitHubStepSummary_PathIsDirectory_ReturnsFalseWithoutThrowing() =>
        await Assert.That(CoverageReportHelpers.TryAppendGitHubStepSummary(_root, "# md")).IsFalse();

    [Test]
    public async Task TryAppendGitHubStepSummary_MissingParentDirectory_ReturnsFalseWithoutThrowing() =>
        await Assert.That(CoverageReportHelpers.TryAppendGitHubStepSummary(
            Path.Combine(_root, "no-such-dir", "summary.md"), "# md")).IsFalse();
}
