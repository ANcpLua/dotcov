using System.Text.Json;
using DotCov.Tests.Infrastructure;
using DotCov.Tool;

namespace DotCov.Tests;
/// <summary>
/// <c>--exclude-test-code</c> on every command that takes an exclusion, through the one
/// predicate <c>--exclude-generated</c> uses, and the rule that an active exclusion is never
/// silent: stderr names its rules and files for table and markdown output, JSON carries
/// <c>excluded</c> and <c>exclusionRules</c>. The Fallout parameter is pinned in
/// <see cref = "FalloutBuildTests"/>.
/// </summary>
[TestClass]
public sealed class ExcludeTestCodeTests(TestContext testContext) : IDisposable
{
    private const string TestFile = "tests/App.Tests/WidgetTests.cs";
    private const string TestCodeRules = "exclusion rules: .Tests/, .Tests\\, TestSupport/, TestSupport\\, /TestFixtures/, \\TestFixtures\\";
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-exclude-tests-");
    public void Dispose() => _ws.Dispose();
    private static async Task<(int Code, string StdOut, string StdErr)> Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await DotCovCli.RunAsync(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    /// <summary>Product code at 50%, test code fully covered by construction — it lifts the total to 75%.</summary>
    private string Mixed() => _ws.Write("mixed.cobertura.xml", Cobertura.NewDoc().AddClass("src/App/Widget.cs", "App.Widget", c => c.Method("Draw", "()", "4", m => m.Line(1, 1).Line(2, 0)).Line(1, 1).Line(2, 0)).AddClass(TestFile, "App.Tests.WidgetTests", c => c.Method("Draws", "()", "1", m => m.Line(1, 1).Line(2, 1)).Line(1, 1).Line(2, 1)));
    [TestMethod]
    public async Task Report_Table_DropsTestCode_AndNamesTheExclusionOnStderr()
    {
        var(code, stdout, stderr) = await Run("report", Mixed(), "--exclude-test-code");
        Assert.AreEqual(0, code);
        Assert.DoesNotContain("WidgetTests", stdout);
        Assert.Contains("50.0%", stdout);
        Assert.AreEqual(string.Join(Environment.NewLine, TestCodeRules, "excluded 1 files:", $"  {TestFile}", ""), stderr);
    }

    [TestMethod]
    public async Task Report_Markdown_NamesTheExclusionOnStderr()
    {
        var(code, stdout, stderr) = await Run("report", Mixed(), "--exclude-test-code", "--format", "md");
        Assert.AreEqual(0, code);
        Assert.DoesNotContain("WidgetTests", stdout);
        Assert.StartsWith(TestCodeRules, stderr);
        Assert.Contains($"  {TestFile}", stderr);
    }

    [TestMethod]
    public async Task Report_Json_CarriesExcludedAndExclusionRules_AndStderrStaysQuiet()
    {
        var(code, stdout, stderr) = await Run("report", Mixed(), "--exclude-test-code", "--format", "json");
        Assert.AreEqual(0, code);
        Assert.IsEmpty(stderr);
        using var json = JsonDocument.Parse(stdout);
        Assert.AreSequenceEqual([TestFile], json.RootElement.GetProperty("excluded").EnumerateArray().Select(static e => e.GetString()!), SequenceOrder.InAnyOrder);
        Assert.AreSequenceEqual(ExclusionRules.TestCode, json.RootElement.GetProperty("exclusionRules").EnumerateArray().Select(static e => e.GetString()!), SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    public async Task Report_WithoutAnExclusion_JsonHasNeitherKey()
    {
        var(_, stdout, _) = await Run("report", Mixed(), "--format", "json");
        using var json = JsonDocument.Parse(stdout);
        Assert.IsFalse(json.RootElement.TryGetProperty("excluded", out _));
        Assert.IsFalse(json.RootElement.TryGetProperty("exclusionRules", out _));
    }

    [TestMethod]
    public async Task Check_GatesWithoutTestCode_AndNamesTheExclusionAfterTheVerdict()
    {
        var(without, _, _) = await Run("check", Mixed(), "--min-line", "70");
        var(code, stdout, stderr) = await Run("check", Mixed(), "--min-line", "70", "--exclude-test-code");
        Assert.AreEqual(0, without); // 75%: the tests' own lines carried it
        Assert.AreEqual(1, code);
        Assert.IsEmpty(stdout);
        Assert.StartsWith("FAIL: line 50.0% (min 70%)", stderr);
        Assert.Contains(TestCodeRules, stderr);
        Assert.Contains($"  {TestFile}", stderr);
    }

    [TestMethod]
    public async Task Check_Pass_NamesTheExclusionOnStderr()
    {
        var(code, stdout, stderr) = await Run("check", Mixed(), "--min-line", "50", "--exclude-test-code");
        Assert.AreEqual(0, code);
        Assert.IsEmpty(stdout);
        // The verdict leads stderr; the exclusion it was computed under follows it.
        var lines = stderr.Split(Environment.NewLine);
        Assert.StartsWith("PASS: line 50.0% (min 50%)", lines[0]);
        Assert.AreEqual(TestCodeRules, lines[1]);
    }

    [TestMethod]
    [DataRow("table")]
    [DataRow("md")]
    public async Task Crap_DropsTestCodeMethods_AndNamesTheExclusionOnStderr(string format)
    {
        var(code, stdout, stderr) = await Run("crap", Mixed(), "--exclude-test-code", "--format", format);
        Assert.AreEqual(0, code);
        Assert.DoesNotContain("WidgetTests", stdout);
        Assert.Contains("App.Widget.Draw", stdout);
        Assert.Contains(TestCodeRules, stderr);
        Assert.Contains($"  {TestFile}", stderr);
    }

    [TestMethod]
    public async Task Crap_Json_CarriesExcludedAndExclusionRules()
    {
        var(code, stdout, stderr) = await Run("crap", Mixed(), "--exclude-test-code", "--format", "json");
        Assert.AreEqual(0, code);
        // The verdict alone: JSON carries the exclusion, so stderr does not repeat it.
        Assert.ContainsSingle(stderr.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith("PASS: worst CRAP ", stderr);
        using var json = JsonDocument.Parse(stdout);
        Assert.AreSequenceEqual([TestFile], json.RootElement.GetProperty("excluded").EnumerateArray().Select(static e => e.GetString()!), SequenceOrder.InAnyOrder);
        Assert.AreEqual(ExclusionRules.TestCode.Count, json.RootElement.GetProperty("exclusionRules").GetArrayLength());
    }

    [TestMethod]
    public async Task Snapshot_CarriesExcludedAndExclusionRules()
    {
        var(code, stdout, stderr) = await Run("snapshot", Mixed(), "--exclude-test-code", "--commit", "abc", "--branch", "main", "--project", "App");
        Assert.AreEqual(0, code);
        Assert.IsEmpty(stderr);
        using var json = JsonDocument.Parse(stdout);
        Assert.AreEqual(TestFile, json.RootElement.GetProperty("excluded")[0].GetString());
        Assert.AreEqual(1, json.RootElement.GetProperty("files").GetArrayLength());
    }

    [TestMethod]
    public async Task Keep_OverridesTestCodeExclusion_LikeItOverridesGenerated()
    {
        // --keep needs no --exclude-generated beside --exclude-test-code.
        var(code, stdout, stderr) = await Run("report", Mixed(), "--exclude-test-code", "--keep", "WidgetTests.cs");
        Assert.AreEqual(0, code);
        Assert.Contains("WidgetTests", stdout);
        Assert.AreEqual(string.Join(Environment.NewLine, TestCodeRules, "kept by --keep: WidgetTests.cs", "excluded 0 files:", ""), stderr);
    }

    [TestMethod]
    public async Task BothExclusions_ApplyBothRuleSets_ThroughOnePredicate()
    {
        var path = _ws.Write("both.cobertura.xml", Cobertura.NewDoc().AddClass("src/App/Widget.cs", c => c.Line(1, 1)).AddClass("src/App/obj/Gen.g.cs", c => c.Line(1, 0)).AddClass("TestSupport/Fixture.cs", c => c.Line(1, 0)));
        var(code, stdout, stderr) = await Run("report", path, "--exclude-generated", "--exclude-test-code", "--format", "json");
        Assert.AreEqual(0, code, message: stderr);
        using var json = JsonDocument.Parse(stdout);
        Assert.AreSequenceEqual(["src/App/obj/Gen.g.cs", "TestSupport/Fixture.cs"], json.RootElement.GetProperty("excluded").EnumerateArray().Select(static e => e.GetString()!), SequenceOrder.InAnyOrder);
        Assert.AreSequenceEqual([..ExclusionRules.WellKnown, ..ExclusionRules.TestCode], json.RootElement.GetProperty("exclusionRules").EnumerateArray().Select(static e => e.GetString()!), SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    public async Task ExclusionFlag_OnACommandThatTakesNone_IsAUsageError()
    {
        var(code, _, stderr) = await Run("version", "--exclude-test-code");
        Assert.AreEqual(DotCovCli.UsageExitCode, code);
        Assert.StartsWith("error: Unknown option '--exclude-test-code' for 'version'.", stderr);
    }

    [TestMethod]
    public async Task ExclusionFlag_TakesNoValue_SoAFollowingPathStaysPositional()
    {
        var(code, stdout, _) = await Run("report", "--exclude-test-code", Mixed());
        Assert.AreEqual(0, code);
        Assert.DoesNotContain("WidgetTests", stdout);
    }

    [TestMethod]
    public async Task Test_GatesWithoutTestCode_AfterASuccessfulRun()
    {
        var cancellationToken = testContext.CancellationToken;
        var dotnet = FakeDotnet.Write(_ws, Mixed());
        var run = await DotcovProcess.RunAsync(_ws.Root, new Dictionary<string, string?> { ["DOTNET_HOST_PATH"] = dotnet }, cancellationToken, "test", "--min-line", "70", "--exclude-test-code");
        Assert.AreEqual(1, run.ExitCode, message: run.Output);
        Assert.DoesNotContain("WidgetTests", run.StdOut);
        Assert.StartsWith("FAIL: line 50.0% (min 70%)", run.StdErr);
        Assert.Contains(TestCodeRules, run.StdErr);
    }

    // ── The library side: what an exclusion records ──
    [TestMethod]
    public void Exclude_Twice_AccumulatesRulesAndFiles_AndMergeReconcilesThem()
    {
        var report = Cobertura.NewDoc().AddClass("src/A.cs", c => c.Line(1, 1)).AddClass("src/obj/B.g.cs", c => c.Line(1, 1)).AddClass("tests/A.Tests/ATests.cs", c => c.Line(1, 1)).Parse();
        var once = report.Exclude(ExclusionRules.WellKnown, ["Program.cs"]);
        var twice = once.Exclude(ExclusionRules.TestCode, []);
        var merged = CoverageReport.Merge(twice, report.Exclude(ExclusionRules.WellKnown));
        Assert.AreSame(FileExclusion.None, report.Exclusion);
        Assert.IsFalse(report.Exclusion.IsActive);
        Assert.AreSequenceEqual(["src/A.cs"], twice.Files.Select(static f => f.Path), SequenceOrder.InAnyOrder);
        Assert.AreSequenceEqual(["src/obj/B.g.cs", "tests/A.Tests/ATests.cs"], twice.Exclusion.ExcludedFiles, SequenceOrder.InAnyOrder);
        Assert.AreSequenceEqual([..ExclusionRules.WellKnown, ..ExclusionRules.TestCode], twice.Exclusion.Patterns, SequenceOrder.InAnyOrder);
        Assert.AreSequenceEqual(["Program.cs"], twice.Exclusion.Keep, SequenceOrder.InAnyOrder);
        Assert.AreSequenceEqual(["src/A.cs", "tests/A.Tests/ATests.cs"], merged.Files.Select(static f => f.Path), SequenceOrder.InAnyOrder);
        Assert.AreSequenceEqual(["src/obj/B.g.cs"], merged.Exclusion.ExcludedFiles, SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void Merge_ReintroducedFilesAreNotExcluded_AndAbsentPathsKeepTheirExactIdentity(bool excludedFirst)
    {
        var original = Cobertura.NewDoc().AddClass(TestFile, c => c.Line(1, 1)).AddClass("src/Only.g.cs", c => c.Line(2, 0)).Parse();
        var excluded = original.Exclude([TestFile, "Only.g.cs"], ["Program.cs"]);
        var included = Cobertura.NewDoc().AddClass(TestFile, c => c.Line(1, 1)).AddClass("src/only.g.cs", c => c.Line(3, 0)).Parse();
        var merged = excludedFirst ? CoverageReport.Merge(excluded, included) : CoverageReport.Merge(included, excluded);
        Assert.AreEqual(2, merged.TotalLines);
        Assert.AreEqual(1, merged.TotalLinesHit);
        Assert.AreSequenceEqual(["src/Only.g.cs"], merged.Exclusion.ExcludedFiles, SequenceOrder.InAnyOrder);
        Assert.AreSequenceEqual([TestFile, "Only.g.cs"], merged.Exclusion.Patterns, SequenceOrder.InAnyOrder);
        Assert.AreSequenceEqual(["Program.cs"], merged.Exclusion.Keep, SequenceOrder.InAnyOrder);
        Assert.AreSequenceEqual([TestFile, "src/Only.g.cs"], excluded.Exclusion.ExcludedFiles, SequenceOrder.InAnyOrder);
        using var json = JsonDocument.Parse(Formatters.JsonFormatter.Format(merged));
        Assert.AreSequenceEqual(["src/Only.g.cs"], json.RootElement.GetProperty("excluded").EnumerateArray().Select(static e => e.GetString()!), SequenceOrder.InAnyOrder);
    }
}
