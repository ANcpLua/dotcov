using System.Text.Json;
using DotCov.Tests.Infrastructure;
using DotCov.Tool;

namespace DotCov.Tests;

/// <summary>
/// <c>--exclude-test-code</c> on every command that takes an exclusion, through the one
/// predicate <c>--exclude-generated</c> uses, and the rule that an active exclusion is never
/// silent: stderr names its rules and files for table and markdown output, JSON carries
/// <c>excluded</c> and <c>exclusionRules</c>. The Fallout parameter is pinned in
/// <see cref="FalloutBuildTests"/>.
/// </summary>
public sealed class ExcludeTestCodeTests : IDisposable
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
    private string Mixed() => _ws.Write("mixed.cobertura.xml", Cobertura.NewDoc()
        .AddClass("src/App/Widget.cs", "App.Widget", c => c
            .Method("Draw", "()", "4", m => m.Line(1, 1).Line(2, 0))
            .Line(1, 1).Line(2, 0))
        .AddClass(TestFile, "App.Tests.WidgetTests", c => c
            .Method("Draws", "()", "1", m => m.Line(1, 1).Line(2, 1))
            .Line(1, 1).Line(2, 1)));

    [Test]
    public async Task Report_Table_DropsTestCode_AndNamesTheExclusionOnStderr()
    {
        var (code, stdout, stderr) = await Run("report", Mixed(), "--exclude-test-code");

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stdout).DoesNotContain("WidgetTests");
        await Assert.That(stdout).Contains("50.0%");
        await Assert.That(stderr).IsEqualTo(string.Join(Environment.NewLine,
            TestCodeRules, "excluded 1 files:", $"  {TestFile}", ""));
    }

    [Test]
    public async Task Report_Markdown_NamesTheExclusionOnStderr()
    {
        var (code, stdout, stderr) = await Run("report", Mixed(), "--exclude-test-code", "--format", "md");

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stdout).DoesNotContain("WidgetTests");
        await Assert.That(stderr).StartsWith(TestCodeRules);
        await Assert.That(stderr).Contains($"  {TestFile}");
    }

    [Test]
    public async Task Report_Json_CarriesExcludedAndExclusionRules_AndStderrStaysQuiet()
    {
        var (code, stdout, stderr) = await Run("report", Mixed(), "--exclude-test-code", "--format", "json");

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stderr).IsEmpty();
        using var json = JsonDocument.Parse(stdout);
        await Assert.That(json.RootElement.GetProperty("excluded").EnumerateArray().Select(static e => e.GetString()!))
            .IsEquivalentTo([TestFile]);
        await Assert.That(json.RootElement.GetProperty("exclusionRules").EnumerateArray().Select(static e => e.GetString()!))
            .IsEquivalentTo(ExclusionRules.TestCode);
    }

    [Test]
    public async Task Report_WithoutAnExclusion_JsonHasNeitherKey()
    {
        var (_, stdout, _) = await Run("report", Mixed(), "--format", "json");

        using var json = JsonDocument.Parse(stdout);
        await Assert.That(json.RootElement.TryGetProperty("excluded", out _)).IsFalse();
        await Assert.That(json.RootElement.TryGetProperty("exclusionRules", out _)).IsFalse();
    }

    [Test]
    public async Task Check_GatesWithoutTestCode_AndNamesTheExclusionAfterTheVerdict()
    {
        var (without, _, _) = await Run("check", Mixed(), "--min-line", "70");
        var (code, stdout, stderr) = await Run("check", Mixed(), "--min-line", "70", "--exclude-test-code");

        await Assert.That(without).IsEqualTo(0);   // 75%: the tests' own lines carried it
        await Assert.That(code).IsEqualTo(1);
        await Assert.That(stdout).IsEmpty();
        await Assert.That(stderr).StartsWith("FAIL: line 50.0% (min 70%)");
        await Assert.That(stderr).Contains(TestCodeRules);
        await Assert.That(stderr).Contains($"  {TestFile}");
    }

    [Test]
    public async Task Check_Pass_NamesTheExclusionOnStderr()
    {
        var (code, stdout, stderr) = await Run("check", Mixed(), "--min-line", "50", "--exclude-test-code");

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stdout).IsEmpty();
        // The verdict leads stderr; the exclusion it was computed under follows it.
        var lines = stderr.Split(Environment.NewLine);
        await Assert.That(lines[0]).StartsWith("PASS: line 50.0% (min 50%)");
        await Assert.That(lines[1]).IsEqualTo(TestCodeRules);
    }

    [Test]
    [Arguments("table")]
    [Arguments("md")]
    public async Task Crap_DropsTestCodeMethods_AndNamesTheExclusionOnStderr(string format)
    {
        var (code, stdout, stderr) = await Run("crap", Mixed(), "--exclude-test-code", "--format", format);

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stdout).DoesNotContain("WidgetTests");
        await Assert.That(stdout).Contains("App.Widget.Draw");
        await Assert.That(stderr).Contains(TestCodeRules);
        await Assert.That(stderr).Contains($"  {TestFile}");
    }

    [Test]
    public async Task Crap_Json_CarriesExcludedAndExclusionRules()
    {
        var (code, stdout, stderr) = await Run("crap", Mixed(), "--exclude-test-code", "--format", "json");

        await Assert.That(code).IsEqualTo(0);
        // The verdict alone: JSON carries the exclusion, so stderr does not repeat it.
        await Assert.That(stderr.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)).HasSingleItem();
        await Assert.That(stderr).StartsWith("PASS: worst CRAP ");
        using var json = JsonDocument.Parse(stdout);
        await Assert.That(json.RootElement.GetProperty("excluded").EnumerateArray().Select(static e => e.GetString()!))
            .IsEquivalentTo([TestFile]);
        await Assert.That(json.RootElement.GetProperty("exclusionRules").GetArrayLength()).IsEqualTo(ExclusionRules.TestCode.Count);
    }

    [Test]
    public async Task Snapshot_CarriesExcludedAndExclusionRules()
    {
        var (code, stdout, stderr) = await Run(
            "snapshot", Mixed(), "--exclude-test-code", "--commit", "abc", "--branch", "main", "--project", "App");

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stderr).IsEmpty();
        using var json = JsonDocument.Parse(stdout);
        await Assert.That(json.RootElement.GetProperty("excluded")[0].GetString()).IsEqualTo(TestFile);
        await Assert.That(json.RootElement.GetProperty("files").GetArrayLength()).IsEqualTo(1);
    }

    [Test]
    public async Task Keep_OverridesTestCodeExclusion_LikeItOverridesGenerated()
    {
        // --keep needs no --exclude-generated beside --exclude-test-code.
        var (code, stdout, stderr) = await Run("report", Mixed(), "--exclude-test-code", "--keep", "WidgetTests.cs");

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stdout).Contains("WidgetTests");
        await Assert.That(stderr).IsEqualTo(string.Join(Environment.NewLine,
            TestCodeRules, "kept by --keep: WidgetTests.cs", "excluded 0 files:", ""));
    }

    [Test]
    public async Task BothExclusions_ApplyBothRuleSets_ThroughOnePredicate()
    {
        var path = _ws.Write("both.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/App/Widget.cs", c => c.Line(1, 1))
            .AddClass("src/App/obj/Gen.g.cs", c => c.Line(1, 0))
            .AddClass("TestSupport/Fixture.cs", c => c.Line(1, 0)));

        var (code, stdout, stderr) = await Run("report", path, "--exclude-generated", "--exclude-test-code", "--format", "json");

        await Assert.That(code).IsEqualTo(0).Because(stderr);
        using var json = JsonDocument.Parse(stdout);
        await Assert.That(json.RootElement.GetProperty("excluded").EnumerateArray().Select(static e => e.GetString()!))
            .IsEquivalentTo(["src/App/obj/Gen.g.cs", "TestSupport/Fixture.cs"]);
        await Assert.That(json.RootElement.GetProperty("exclusionRules").EnumerateArray().Select(static e => e.GetString()!))
            .IsEquivalentTo([.. ExclusionRules.WellKnown, .. ExclusionRules.TestCode]);
    }

    [Test]
    public async Task ExclusionFlag_OnACommandThatTakesNone_IsAUsageError()
    {
        var (code, _, stderr) = await Run("version", "--exclude-test-code");

        await Assert.That(code).IsEqualTo(DotCovCli.UsageExitCode);
        await Assert.That(stderr).StartsWith("error: Unknown option '--exclude-test-code' for 'version'.");
    }

    [Test]
    public async Task ExclusionFlag_TakesNoValue_SoAFollowingPathStaysPositional()
    {
        var (code, stdout, _) = await Run("report", "--exclude-test-code", Mixed());

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stdout).DoesNotContain("WidgetTests");
    }

    [Test]
    public async Task Test_GatesWithoutTestCode_AfterASuccessfulRun(CancellationToken cancellationToken)
    {
        var dotnet = FakeDotnet.Write(_ws, Mixed());

        var run = await DotcovProcess.RunAsync(cancellationToken, _ws.Root,
            new Dictionary<string, string?> { ["DOTNET_HOST_PATH"] = dotnet },
            "test", "--min-line", "70", "--exclude-test-code");

        await Assert.That(run.ExitCode).IsEqualTo(1).Because(run.Output);
        await Assert.That(run.StdOut).DoesNotContain("WidgetTests");
        await Assert.That(run.StdErr).StartsWith("FAIL: line 50.0% (min 70%)");
        await Assert.That(run.StdErr).Contains(TestCodeRules);
    }

    // ── The library side: what an exclusion records ──

    [Test]
    public async Task Exclude_Twice_AccumulatesRulesAndFiles_AndMergeReconcilesThem()
    {
        var report = Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c.Line(1, 1))
            .AddClass("src/obj/B.g.cs", c => c.Line(1, 1))
            .AddClass("tests/A.Tests/ATests.cs", c => c.Line(1, 1))
            .Parse();

        var once = report.Exclude(ExclusionRules.WellKnown, ["Program.cs"]);
        var twice = once.Exclude(ExclusionRules.TestCode, []);
        var merged = CoverageReport.Merge(twice, report.Exclude(ExclusionRules.WellKnown));

        await Assert.That(report.Exclusion).IsSameReferenceAs(FileExclusion.None);
        await Assert.That(report.Exclusion.IsActive).IsFalse();
        await Assert.That(twice.Files.Select(static f => f.Path)).IsEquivalentTo(["src/A.cs"]);
        await Assert.That(twice.Exclusion.ExcludedFiles).IsEquivalentTo(["src/obj/B.g.cs", "tests/A.Tests/ATests.cs"]);
        await Assert.That(twice.Exclusion.Patterns).IsEquivalentTo([.. ExclusionRules.WellKnown, .. ExclusionRules.TestCode]);
        await Assert.That(twice.Exclusion.Keep).IsEquivalentTo(["Program.cs"]);
        await Assert.That(merged.Files.Select(static f => f.Path)).IsEquivalentTo(["src/A.cs", "tests/A.Tests/ATests.cs"]);
        await Assert.That(merged.Exclusion.ExcludedFiles).IsEquivalentTo(["src/obj/B.g.cs"]);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Merge_ReintroducedFilesAreNotExcluded_AndAbsentPathsKeepTheirExactIdentity(bool excludedFirst)
    {
        var original = Cobertura.NewDoc()
            .AddClass(TestFile, c => c.Line(1, 1))
            .AddClass("src/Only.g.cs", c => c.Line(2, 0))
            .Parse();
        var excluded = original.Exclude([TestFile, "Only.g.cs"], ["Program.cs"]);
        var included = Cobertura.NewDoc()
            .AddClass(TestFile, c => c.Line(1, 1))
            .AddClass("src/only.g.cs", c => c.Line(3, 0))
            .Parse();

        var merged = excludedFirst ? CoverageReport.Merge(excluded, included) : CoverageReport.Merge(included, excluded);

        await Assert.That(merged.TotalLines).IsEqualTo(2);
        await Assert.That(merged.TotalLinesHit).IsEqualTo(1);
        await Assert.That(merged.Exclusion.ExcludedFiles).IsEquivalentTo(["src/Only.g.cs"]);
        await Assert.That(merged.Exclusion.Patterns).IsEquivalentTo([TestFile, "Only.g.cs"]);
        await Assert.That(merged.Exclusion.Keep).IsEquivalentTo(["Program.cs"]);
        await Assert.That(excluded.Exclusion.ExcludedFiles).IsEquivalentTo([TestFile, "src/Only.g.cs"]);
        using var json = JsonDocument.Parse(DotCov.Formatters.JsonFormatter.Format(merged));
        await Assert.That(json.RootElement.GetProperty("excluded").EnumerateArray().Select(static e => e.GetString()!))
            .IsEquivalentTo(["src/Only.g.cs"]);
    }
}
