using System.Text;
using System.Text.Json;
using DotCov.Tests.Infrastructure;
using DotCov.Tool;

namespace DotCov.Tests;

/// <summary>
/// Coverage tools instrument dependencies that ship a PDB (Testcontainers, Moq, bunit, the test
/// platform) and report their files under SourceLink's deterministic root, <c>/_/</c>. Those
/// lines are not the repository's, yet they count toward every rate: a project at 92% read 27%.
/// The report names them in one warning, and <c>--exclude-external</c> removes them.
/// </summary>
public sealed class ExternalSourcesTests : IDisposable
{
    private const string External = "/_/src/Lib/Client.cs";
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-external-");

    public void Dispose() => _ws.Dispose();

    private static string Doc(string? source, params (string File, int Hits)[] files)
    {
        var sources = source is null ? "" : $"<sources><source>{source}</source></sources>";
        var classes = string.Concat(files.Select(static f =>
            $"""<class name="C" filename="{f.File}"><lines><line number="1" hits="{f.Hits}" /></lines></class>"""));
        return $"<coverage>{sources}<packages><package name=\"p\"><classes>{classes}</classes></package></packages></coverage>";
    }

    private static CoverageReport Parse(string xml) =>
        CoberturaParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

    [Test]
    public async Task DependencyFiles_AreNamedInOneWarning_AndAnExclusionRemovesThemAndIt()
    {
        var report = Parse(Doc(null, ("/repo/src/App.cs", 1), (External, 0), ("/_/src/Lib/Other.cs", 0)));

        var warning = await Assert.That(report.Warnings).HasSingleItem();
        await Assert.That(warning.Kind).IsEqualTo(CoverageWarningKind.ExternalSources);
        await Assert.That(warning.File).IsEqualTo(External);
        await Assert.That(warning.Detail).StartsWith("2 files have SourceLink paths (/_/...)");
        await Assert.That(report.Warnings).IsSameReferenceAs(report.Warnings);

        var own = report.Exclude(ExclusionRules.External, []);

        await Assert.That(own.Warnings).IsEmpty();
        await Assert.That(own.Files.Select(static f => f.Path)).IsEquivalentTo(["/repo/src/App.cs"]);
        await Assert.That(own.Exclusion.ExcludedFiles).IsEquivalentTo([External, "/_/src/Lib/Other.cs"]);
    }

    [Test]
    public async Task AReportBuiltDeterministicallyItself_OwnsItsDeterministicPaths()
    {
        var report = Parse(Doc("/_/", ("src/App.cs", 1)));

        await Assert.That(report.Files.Single().Path).StartsWith("/_/");
        await Assert.That(report.Warnings).IsEmpty();
    }

    [Test]
    public async Task OnlyAPathStartingWithTheDeterministicRoot_IsExternal()
    {
        var report = Parse(Doc(null, ("/repo/_/Owned.cs", 0), (External, 0)));

        var own = report.Exclude(ExclusionRules.External, []);

        await Assert.That(report.Warnings.Single().Detail).StartsWith("1 files have SourceLink paths");
        await Assert.That(own.Files.Select(static f => f.Path)).IsEquivalentTo(["/repo/_/Owned.cs"]);
    }

    [Test]
    public async Task ADeterministicReport_KeepsItsOwnFiles_UnderExcludeExternal()
    {
        var report = Parse(Doc("/_/", ("src/App.cs", 0)));

        var excluded = report.Exclude(ExclusionRules.External, []);

        await Assert.That(excluded.Files.Count).IsEqualTo(1);
        await Assert.That(excluded.Exclusion.IsActive).IsFalse();
    }

    [Test]
    public async Task MergingWithADeterministicReport_KeepsTheOtherReportsWarning()
    {
        var merged = CoverageReport.Merge(
            Parse(Doc("/_/", ("src/App.cs", 1))),
            Parse(Doc(null, (External, 0))));

        await Assert.That(merged.SourceRoots.Any(static r => r.TrimEnd('/') == "/_")).IsTrue();
        var warning = await Assert.That(merged.Warnings).HasSingleItem();
        await Assert.That(warning.Kind).IsEqualTo(CoverageWarningKind.ExternalSources);
    }

    [Test]
    public async Task Merging_CountsTheExternalFilesOfBothReportsOnce()
    {
        var merged = CoverageReport.Merge(
            Parse(Doc(null, ("/repo/src/App.cs", 1), (External, 0))),
            Parse(Doc(null, ("/_/src/Lib/Other.cs", 0))));

        var warning = await Assert.That(merged.Warnings).HasSingleItem();
        await Assert.That(warning.Detail).StartsWith("2 files have SourceLink paths");
    }

    [Test]
    public async Task MethodReports_NameAndExcludeExternalMethodsTheSameWay()
    {
        using var input = new MemoryStream(Cobertura.NewDoc()
            .AddClass("/repo/src/App.cs", "App.A", c => c.Method("Kept", "()", "1", m => m.Line(1, 1)))
            .AddClass(External, "Lib.Client", c => c.Method("Call", "()", "9", m => m.Line(1, 0))).ToBytes());

        var methods = CoberturaParser.ParseMethods(input);

        await Assert.That((await Assert.That(methods.Warnings).HasSingleItem()).Kind).IsEqualTo(CoverageWarningKind.ExternalSources);
        var own = methods.Exclude(ExclusionRules.External, []);
        await Assert.That(own.Warnings).IsEmpty();
        await Assert.That(CrapAnalysis.Analyze(own).Methods.Single().Method).IsEqualTo("App.A.Kept");
    }

    [Test]
    public async Task Check_WarnsAboutDependencyFiles_AndExcludeExternalMeasuresTheRepositoryAlone()
    {
        var report = _ws.Write("coverage.cobertura.xml", Doc(null, ("/repo/src/App.cs", 1), (External, 0)));

        var (warnCode, _, warnErr) = await Run("check", report, "--min-line", "80");
        var (code, stdout, stderr) = await Run("check", report, "--min-line", "80", "--exclude-external", "--format", "json");

        await Assert.That(warnCode).IsEqualTo(1).Because(warnErr);
        await Assert.That(warnErr).Contains($"warning: {External}: 1 files have SourceLink paths (/_/...)");
        await Assert.That(warnErr).Contains("--exclude-external");
        await Assert.That(code).IsEqualTo(0).Because(stderr);
        using var json = JsonDocument.Parse(stdout);
        await Assert.That(json.RootElement.GetProperty("summary").GetProperty("lineRate").GetDouble()).IsEqualTo(100);
        await Assert.That(json.RootElement.GetProperty("excluded").EnumerateArray().Select(static e => e.GetString()!))
            .IsEquivalentTo([External]);
        await Assert.That(json.RootElement.GetProperty("warnings").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task Crap_StderrDescribesTheAnalyzedMethods()
    {
        var report = _ws.Write("coverage.cobertura.xml", Cobertura.NewDoc()
            .AddClass("/repo/src/App.cs", "App.A", c => c.Method("Kept", "()", "1", m => m.Line(1, 1)))
            .AddClass(External, "Lib.Client", c => c.Method("Call", "()", "9", m => m.Line(1, 0))).ToBytes());

        var (_, _, warned) = await Run("crap", report);
        var (code, _, stderr) = await Run("crap", report, "--exclude-external");

        await Assert.That(warned).Contains("1 files have SourceLink paths");
        await Assert.That(code).IsEqualTo(0).Because(stderr);
        await Assert.That(stderr).DoesNotContain("SourceLink paths");
        await Assert.That(stderr).Contains(External);   // listed as excluded
    }

    [Test]
    public async Task Json_CarriesTheWarning_WhenNothingIsExcluded()
    {
        var report = _ws.Write("coverage.cobertura.xml", Doc(null, ("/repo/src/App.cs", 1), (External, 0)));

        var (code, stdout, _) = await Run("report", report, "--format", "json");

        await Assert.That(code).IsEqualTo(0);
        using var json = JsonDocument.Parse(stdout);
        var warning = json.RootElement.GetProperty("warnings").EnumerateArray().Single();
        await Assert.That(warning.GetProperty("kind").GetString()).IsEqualTo("ExternalSources");
        await Assert.That(warning.GetProperty("file").GetString()).IsEqualTo(External);
    }

    [Test]
    public async Task Keep_AcceptsExcludeExternal_AsItsExclusion()
    {
        var report = _ws.Write("coverage.cobertura.xml", Doc(null, ("/repo/src/App.cs", 1), (External, 0)));

        var (code, _, stderr) = await Run("check", report, "--min-line", "80", "--exclude-external", "--keep", "Client.cs");

        // The kept dependency file still counts: 1 of 2 lines.
        await Assert.That(code).IsEqualTo(1).Because(stderr);
        await Assert.That(stderr).StartsWith("FAIL: line 50.0% (min 80%)");
    }

    private static async Task<(int Code, string StdOut, string StdErr)> Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await DotCovCli.RunAsync(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }
}
