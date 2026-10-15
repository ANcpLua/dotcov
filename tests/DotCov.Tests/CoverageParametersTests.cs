using DotCov.Tests.Infrastructure;
using DotCov.Fallout;

namespace DotCov.Tests;
/// <summary>
/// The parameter grammar of <see cref = "ICoverageReport"/>, validated once at the build
/// boundary by <see cref = "CoverageParameters"/>: no default threshold, percentages from 0 to
/// 100, digits-only cap, strict booleans, case-sensitive formats with the <c>md</c> alias, the
/// pattern shapes, and one file-identity mode. The consuming-build tests in
/// <see cref = "FalloutBuildTests"/> prove the same grammar through a real Fallout process.
/// </summary>
[TestClass]
public sealed class CoverageParametersTests
{
    private static CoverageParameters ParseWithDefaults(string? minLine, string? minBranch, string probeSources = CoverageParameters.DefaultProbeSources, string? sourceRoot = null) => CoverageParameters.Parse(minLine, minBranch, CoverageParameters.DefaultFormat, CoverageParameters.DefaultExcludeGenerated, CoverageParameters.DefaultExcludeTestCode, CoverageParameters.DefaultExcludeExternal, CoverageParameters.DefaultPattern, CoverageParameters.DefaultMaxChars, probeSources, sourceRoot);
    [TestMethod]
    public void Parse_OneThreshold_ChecksItAndDisablesTheOther_WithTheDocumentedDefaults()
    {
        var p = ParseWithDefaults("80", minBranch: null);
        Assert.AreEqual(new CoverageParameters(80, 0, CoverageFormat.Table, false, false, false, ReportPattern.Default, 50_000_000, FileIdentity.Lexical), p);
        Assert.AreEqual(70, ParseWithDefaults(minLine: null, "70").MinBranch);
        Assert.AreEqual(0, ParseWithDefaults(minLine: null, "70").MinLine);
    }

    [TestMethod]
    public void Parse_NoThreshold_FailsNamingBothParameters()
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(() => ParseWithDefaults(minLine: null, minBranch: null));
        Assert.Contains("Missing Coverage MinLine and Coverage MinBranch", ex.Message);
        Assert.Contains("--coverage-min-line", ex.Message);
    }

    [TestMethod]
    public void ExclusionPatterns_SelectEachRuleSetByItsFlag()
    {
        static IReadOnlyList<string> Patterns(string generated, string testCode, string external = "false") => CoverageParameters.Parse("80", null, "table", generated, testCode, external, CoverageParameters.DefaultPattern, CoverageParameters.DefaultMaxChars, "false", null).ExclusionPatterns;
        Assert.IsEmpty(Patterns("false", "false"));
        Assert.AreSequenceEqual(ExclusionRules.WellKnown, Patterns("true", "false"), SequenceOrder.InAnyOrder);
        Assert.AreSequenceEqual(ExclusionRules.TestCode, Patterns("false", "true"), SequenceOrder.InAnyOrder);
        Assert.AreSequenceEqual(ExclusionRules.External, Patterns("false", "false", "true"), SequenceOrder.InAnyOrder);
        Assert.AreSequenceEqual([..ExclusionRules.WellKnown, ..ExclusionRules.TestCode, ..ExclusionRules.External], Patterns("true", "true", "true"), SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    public void ParseIdentity_SelectsOneOfTheThreeModes()
    {
        Assert.AreSame(FileIdentity.Lexical, CoverageParameters.ParseIdentity(false, null));
        Assert.AreSame(FileIdentity.Lexical, CoverageParameters.ParseIdentity(false, "  "));
        Assert.AreSame(FileIdentity.ProbeSources, CoverageParameters.ParseIdentity(true, null));
        var explicitRoot = CoverageParameters.ParseIdentity(false, @"c:\repo\src\");
        Assert.AreEqual(FileIdentityMode.SourceRoot, explicitRoot.Mode);
        Assert.AreEqual("C:/repo/src", explicitRoot.SourceRoot);
    }

    [TestMethod]
    public void ParseIdentity_ProbeAndRootTogether_FailsNamingBothParameters()
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(() => ParseWithDefaults("80", null, "true", "/repo"));
        Assert.Contains("Coverage ProbeSourcesParam and Coverage SourceRoot", ex.Message);
    }

    [TestMethod]
    [DataRow("50000000", 50_000_000L)]
    [DataRow("0", 0L)] // 0 = no cap (XmlReaderSettings.MaxCharactersInDocument semantics)
    [DataRow("1024", 1024L)]
    public void ParseMaxChars_ValidValue_Parses(string value, long expected) => Assert.AreEqual(expected, CoverageParameters.ParseMaxChars(value, "Coverage MaxCharsParam"));
    [TestMethod]
    [DataRow("-1")] // digits only, mirroring the CLI's --max-chars: a sign is invalid
    [DataRow("+1")]
    [DataRow("1_000")]
    [DataRow("ten")]
    [DataRow("")]
    public void ParseMaxChars_Garbage_ThrowsNamingTheParameter(string value)
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(() => CoverageParameters.ParseMaxChars(value, "Coverage MaxCharsParam"));
        Assert.Contains("Coverage MaxCharsParam", ex.Message);
        Assert.Contains($"'{value}'", ex.Message);
    }

    [TestMethod]
    [DataRow("80", 80.0)]
    [DataRow("0", 0.0)]
    [DataRow("72.5", 72.5)]
    [DataRow("100", 100.0)]
    public void ParseThreshold_ValidNumber_Parses(string value, double expected) => Assert.AreEqual(expected, CoverageParameters.ParseThreshold(value, "Coverage MinLine"));
    [TestMethod]
    [DataRow("eighty")]
    [DataRow("80,5")] // invariant culture: comma is not a decimal separator
    [DataRow("")]
    [DataRow("NaN")]
    [DataRow("Infinity")]
    [DataRow("-1")]
    [DataRow("100.5")]
    public void ParseThreshold_Garbage_ThrowsNamingTheParameter(string value)
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(() => CoverageParameters.ParseThreshold(value, "Coverage MinLine"));
        Assert.Contains("Coverage MinLine", ex.Message);
        Assert.Contains($"'{value}'", ex.Message);
    }

    [TestMethod]
    [DataRow("true", true)]
    [DataRow("True", true)]
    [DataRow("false", false)]
    [DataRow("FALSE", false)]
    public void ParseFlag_TrueOrFalse_Parses(string value, bool expected) => Assert.AreEqual(expected, CoverageParameters.ParseFlag(value, "Coverage ExcludeGeneratedParam"));
    [TestMethod]
    [DataRow("1")]
    [DataRow("yes")]
    [DataRow("on")]
    [DataRow("")]
    public void ParseFlag_TruthySpelling_FailsLoudlyInsteadOfSilentFalse(string value)
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(() => CoverageParameters.ParseFlag(value, "Coverage ExcludeGeneratedParam"));
        Assert.Contains("Coverage ExcludeGeneratedParam", ex.Message);
        Assert.Contains($"'{value}'", ex.Message);
    }

    [TestMethod]
    [DataRow("table", "Table")]
    [DataRow("json", "Json")]
    [DataRow("markdown", "Markdown")]
    [DataRow("md", "Markdown")] // alias canonicalizes
    public void ParseFormat_KnownFormat_ReturnsCanonicalFormat(string value, string expected) => Assert.AreEqual(expected, CoverageParameters.ParseFormat(value, "Coverage Format").ToString());
    [TestMethod]
    [DataRow("markdwon")]
    [DataRow("xml")]
    [DataRow("TABLE")] // format names are case-sensitive
    [DataRow("")]
    public void ParseFormat_UnknownFormat_FailsLoudlyInsteadOfSilentTable(string value)
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(() => CoverageParameters.ParseFormat(value, "Coverage Format"));
        Assert.Contains("Coverage Format", ex.Message);
        Assert.Contains($"'{value}'", ex.Message);
    }

    [TestMethod]
    [DataRow("coverage.xml", false)]
    [DataRow("**/coverage.xml", true)]
    public void ParsePattern_SupportedShape_Parses(string value, bool recursive)
    {
        var pattern = CoverageParameters.ParsePattern(value, "Coverage Pattern");
        Assert.AreEqual(recursive, pattern.Recursive);
        Assert.AreEqual("coverage.xml", pattern.FileName);
    }

    [TestMethod]
    [DataRow("cov/*.xml")]
    [DataRow("")]
    public void ParsePattern_UnsupportedShape_ThrowsNamingTheParameter(string value)
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(() => CoverageParameters.ParsePattern(value, "Coverage Pattern"));
        Assert.Contains("Invalid Coverage Pattern", ex.Message);
        Assert.Contains($"'{value}'", ex.Message);
    }

    [TestMethod]
    public void Parse_NegativeMaxChars_IsBlamedOnMaxChars_NotThePattern()
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(() => CoverageParameters.Parse("80", "0", "table", "false", "false", "false", "coverage.cobertura.xml", "-1", "false", null));
        Assert.Contains("Coverage MaxCharsParam", ex.Message);
        Assert.DoesNotContain("Coverage Pattern", ex.Message);
    }
}

[TestClass]
public sealed class GitHubStepSummaryTests : IDisposable
{
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-summary-");
    public void Dispose() => _ws.Dispose();
    [TestMethod]
    public async Task TryAppend_WritablePath_Appends()
    {
        var path = _ws.PathOf("summary.md");
        Assert.IsTrue(GitHubStepSummary.TryAppend(path, "one"));
        Assert.IsTrue(GitHubStepSummary.TryAppend(path, "two"));
        Assert.AreEqual("onetwo", await File.ReadAllTextAsync(path));
    }

    [TestMethod]
    public void TryAppend_PathIsDirectory_ReturnsFalseWithoutThrowing() => Assert.IsFalse(GitHubStepSummary.TryAppend(_ws.Root, "# md"));
    [TestMethod]
    public void TryAppend_MissingParentDirectory_ReturnsFalseWithoutThrowing() => Assert.IsFalse(GitHubStepSummary.TryAppend(_ws.PathOf("no-such-dir/summary.md"), "# md"));
}
