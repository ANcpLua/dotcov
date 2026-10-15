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
[TestClass]
public sealed class ExternalSourcesTests : IDisposable
{
    private const string External = "/_/src/Lib/Client.cs";
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-external-");
    public void Dispose() => _ws.Dispose();
    private static string Doc(string? source, params (string File, int Hits)[] files)
    {
        var sources = source is null ? "" : $"<sources><source>{source}</source></sources>";
        var classes = string.Concat(files.Select(static f => $"""<class name="C" filename="{f.File}"><lines><line number="1" hits="{f.Hits}" /></lines></class>"""));
        return $"<coverage>{sources}<packages><package name=\"p\"><classes>{classes}</classes></package></packages></coverage>";
    }

    private static CoverageReport Parse(string xml) => CoberturaParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)));
    [TestMethod]
    public void DependencyFiles_AreNamedInOneWarning_AndAnExclusionRemovesThemAndIt()
    {
        var report = Parse(Doc(null, ("/repo/src/App.cs", 1), (External, 0), ("/_/src/Lib/Other.cs", 0)));
        var warning = Assert.ContainsSingle(report.Warnings);
        Assert.AreEqual(CoverageWarningKind.ExternalSources, warning.Kind);
        Assert.AreEqual(External, warning.File);
        Assert.StartsWith("2 files have SourceLink paths (/_/...)", warning.Detail);
        var cachedWarnings = report.Warnings;
        Assert.AreSame(cachedWarnings, report.Warnings);
        var own = report.Exclude(ExclusionRules.External, []);
        Assert.IsEmpty(own.Warnings);
        Assert.AreSequenceEqual(["/repo/src/App.cs"], own.Files.Select(static f => f.Path), SequenceOrder.InAnyOrder);
        Assert.AreSequenceEqual([External, "/_/src/Lib/Other.cs"], own.Exclusion.ExcludedFiles, SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    public void AReportBuiltDeterministicallyItself_OwnsItsDeterministicPaths()
    {
        var report = Parse(Doc("/_/", ("src/App.cs", 1)));
        Assert.StartsWith("/_/", report.Files.Single().Path);
        Assert.IsEmpty(report.Warnings);
    }

    [TestMethod]
    public void OnlyAPathStartingWithTheDeterministicRoot_IsExternal()
    {
        var report = Parse(Doc(null, ("/repo/_/Owned.cs", 0), (External, 0)));
        var own = report.Exclude(ExclusionRules.External, []);
        Assert.StartsWith("1 files have SourceLink paths", report.Warnings.Single().Detail);
        Assert.AreSequenceEqual(["/repo/_/Owned.cs"], own.Files.Select(static f => f.Path), SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    public void ADeterministicReport_KeepsItsOwnFiles_UnderExcludeExternal()
    {
        var report = Parse(Doc("/_/", ("src/App.cs", 0)));
        var excluded = report.Exclude(ExclusionRules.External, []);
        Assert.AreEqual(1, excluded.Files.Count);
        Assert.IsFalse(excluded.Exclusion.IsActive);
    }

    [TestMethod]
    public void MergingWithADeterministicReport_KeepsTheOtherReportsWarning()
    {
        var merged = CoverageReport.Merge(Parse(Doc("/_/", ("src/App.cs", 1))), Parse(Doc(null, (External, 0))));
        Assert.IsTrue(merged.SourceRoots.Any(static r => r.TrimEnd('/') == "/_"));
        var warning = Assert.ContainsSingle(merged.Warnings);
        Assert.AreEqual(CoverageWarningKind.ExternalSources, warning.Kind);
    }

    [TestMethod]
    public void Merging_CountsTheExternalFilesOfBothReportsOnce()
    {
        var merged = CoverageReport.Merge(Parse(Doc(null, ("/repo/src/App.cs", 1), (External, 0))), Parse(Doc(null, ("/_/src/Lib/Other.cs", 0))));
        var warning = Assert.ContainsSingle(merged.Warnings);
        Assert.StartsWith("2 files have SourceLink paths", warning.Detail);
    }

    [TestMethod]
    public void MethodReports_NameAndExcludeExternalMethodsTheSameWay()
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("/repo/src/App.cs", "App.A", c => c.Method("Kept", "()", "1", m => m.Line(1, 1))).AddClass(External, "Lib.Client", c => c.Method("Call", "()", "9", m => m.Line(1, 0))).ToBytes());
        var methods = CoberturaParser.ParseMethods(input);
        Assert.AreEqual(CoverageWarningKind.ExternalSources, (Assert.ContainsSingle(methods.Warnings)).Kind);
        var own = methods.Exclude(ExclusionRules.External, []);
        Assert.IsEmpty(own.Warnings);
        Assert.AreEqual("App.A.Kept", CrapAnalysis.Analyze(own).Methods.Single().Method);
    }

    [TestMethod]
    public async Task Check_WarnsAboutDependencyFiles_AndExcludeExternalMeasuresTheRepositoryAlone()
    {
        var report = _ws.Write("coverage.cobertura.xml", Doc(null, ("/repo/src/App.cs", 1), (External, 0)));
        var(warnCode, _, warnErr) = await Run("check", report, "--min-line", "80");
        var(code, stdout, stderr) = await Run("check", report, "--min-line", "80", "--exclude-external", "--format", "json");
        Assert.AreEqual(1, warnCode, message: warnErr);
        Assert.Contains($"warning: {External}: 1 files have SourceLink paths (/_/...)", warnErr);
        Assert.Contains("--exclude-external", warnErr);
        Assert.AreEqual(0, code, message: stderr);
        using var json = JsonDocument.Parse(stdout);
        Assert.AreEqual(100, json.RootElement.GetProperty("summary").GetProperty("lineRate").GetDouble());
        Assert.AreSequenceEqual([External], json.RootElement.GetProperty("excluded").EnumerateArray().Select(static e => e.GetString()!), SequenceOrder.InAnyOrder);
        Assert.AreEqual(0, json.RootElement.GetProperty("warnings").GetArrayLength());
    }

    [TestMethod]
    public async Task Crap_StderrDescribesTheAnalyzedMethods()
    {
        var report = _ws.Write("coverage.cobertura.xml", Cobertura.NewDoc().AddClass("/repo/src/App.cs", "App.A", c => c.Method("Kept", "()", "1", m => m.Line(1, 1))).AddClass(External, "Lib.Client", c => c.Method("Call", "()", "9", m => m.Line(1, 0))).ToBytes());
        var(_, _, warned) = await Run("crap", report);
        var(code, _, stderr) = await Run("crap", report, "--exclude-external");
        Assert.Contains("1 files have SourceLink paths", warned);
        Assert.AreEqual(0, code, message: stderr);
        Assert.DoesNotContain("SourceLink paths", stderr);
        Assert.Contains(External, stderr); // listed as excluded
    }

    [TestMethod]
    public async Task Json_CarriesTheWarning_WhenNothingIsExcluded()
    {
        var report = _ws.Write("coverage.cobertura.xml", Doc(null, ("/repo/src/App.cs", 1), (External, 0)));
        var(code, stdout, _) = await Run("report", report, "--format", "json");
        Assert.AreEqual(0, code);
        using var json = JsonDocument.Parse(stdout);
        var warning = json.RootElement.GetProperty("warnings").EnumerateArray().Single();
        Assert.AreEqual("ExternalSources", warning.GetProperty("kind").GetString());
        Assert.AreEqual(External, warning.GetProperty("file").GetString());
    }

    [TestMethod]
    public async Task Keep_AcceptsExcludeExternal_AsItsExclusion()
    {
        var report = _ws.Write("coverage.cobertura.xml", Doc(null, ("/repo/src/App.cs", 1), (External, 0)));
        var(code, _, stderr) = await Run("check", report, "--min-line", "80", "--exclude-external", "--keep", "Client.cs");
        // The kept dependency file still counts: 1 of 2 lines.
        Assert.AreEqual(1, code, message: stderr);
        Assert.StartsWith("FAIL: line 50.0% (min 80%)", stderr);
    }

    private static async Task<(int Code, string StdOut, string StdErr)> Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await DotCovCli.RunAsync(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }
}
