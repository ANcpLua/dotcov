using System.Text.Json;
using DotCov.Tests.Infrastructure;
using TUnit.Assertions.Enums;
using DotCov.Tool;

namespace DotCov.Tests;

/// <summary>
/// The JSON contract every command shares, pinned through <see cref="DotCovCli.RunAsync"/>: each
/// document opens with <c>schemaVersion</c> and <c>command</c>, names the reports it merged in
/// <c>inputs</c>, carries the parser's <c>warnings</c>, escapes nothing a JSON parser does not
/// need escaped, and ends with a newline. check takes <c>--format json</c> and prints the report
/// document plus a <c>gate</c> object that answers what to fix next.
/// </summary>
public sealed class CliJsonContractTests : IDisposable
{
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-cli-json-");

    public void Dispose() => _ws.Dispose();

    private static async Task<(int Code, string StdOut, string StdErr)> Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await DotCovCli.RunAsync(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    /// <summary>
    /// A: 4/7 lines (57.1%), 1/6 branches, uncovered 21,23-24, partial branches 20 (1/2) and 26 (0/4).
    /// B: 1/2 lines, no branch data. C: 3/3 lines, 1/2 branches. Totals: 8/12 lines, 2/8 branches.
    /// </summary>
    private string ThreeFiles(string relative = "three.cobertura.xml") =>
        _ws.Write(relative, Cobertura.NewDoc()
            .AddClass("src/C.cs", c => c.Line(1, hits: 1).Line(2, hits: 1).Branch(3, "50% (1/2)"))
            .AddClass("src/A.cs", c => c
                .Line(19, hits: 1).Branch(20, "50% (1/2)").Line(21, hits: 0).Line(22, hits: 1)
                .Line(23, hits: 0).Line(24, hits: 0).Branch(26, "0% (0/4)"))
            .AddClass("src/B.cs", c => c.Line(1, hits: 1).Line(2, hits: 0)));

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    private static string[] Keys(JsonElement element) => [.. element.EnumerateObject().Select(static p => p.Name)];

    // ── Every document opens with schemaVersion and command ──

    [Test]
    [Arguments("report")]
    [Arguments("check")]
    [Arguments("crap")]
    [Arguments("snapshot")]
    public async Task EveryJsonDocument_OpensWithSchemaVersionAndCommand(string command)
    {
        var report = _ws.Write("m.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/A.cs", "MyApp.A", c => c.Method("M", "()", "1", m => m.Line(1, hits: 1))));
        string[] args = command switch
        {
            "check" => ["check", report, "--min-line", "50", "--format", "json"],
            "snapshot" => ["snapshot", report, "--commit", "c", "--branch", "b", "--project", "p"],
            _ => [command, report, "--format", "json"]
        };

        var (code, stdout, stderr) = await Run(args);

        await Assert.That(code).IsEqualTo(0).Because(stderr);
        var root = Parse(stdout);
        await Assert.That(Keys(root).Take(4)).IsEquivalentTo(["schemaVersion", "command", "inputs", "warnings"], CollectionOrdering.Matching);
        await Assert.That(root.GetProperty("schemaVersion").GetInt32()).IsEqualTo(1);
        await Assert.That(root.GetProperty("command").GetString()).IsEqualTo(command);
        await Assert.That(root.GetProperty("inputs").EnumerateArray().Select(static i => i.GetString()!)).IsEquivalentTo([report], CollectionOrdering.Matching);
        await Assert.That(root.GetProperty("warnings").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task Diff_Json_ListsTheInputsOfBothSides_AndTheirWarnings()
    {
        var before = _ws.Write("before.xml", Cobertura.NewDoc().AddClass("src/A.cs", c => c.MalformedLine("1", "lots").Line(2, hits: 1)));
        var after = _ws.Write("after.xml", Cobertura.NewDoc().AddClass("src/A.cs", c => c.Line(2, hits: 1)));

        var (code, stdout, _) = await Run("diff", before, after, "--format", "json");

        await Assert.That(code).IsEqualTo(0);
        var root = Parse(stdout);
        await Assert.That(Keys(root).Take(4)).IsEquivalentTo(["schemaVersion", "command", "inputs", "warnings"], CollectionOrdering.Matching);
        await Assert.That(root.GetProperty("command").GetString()).IsEqualTo("diff");
        await Assert.That(root.GetProperty("inputs").EnumerateArray().Select(static i => i.GetString()!)).IsEquivalentTo([before, after], CollectionOrdering.Matching);
        var warning = root.GetProperty("warnings").EnumerateArray().Single();
        await Assert.That(warning.GetProperty("kind").GetString()).IsEqualTo("MalformedHits");
        await Assert.That(warning.GetProperty("detail").GetString()).Contains("lots");
    }

    [Test]
    public async Task Inputs_NameEveryMergedReport_AsStderrDoes()
    {
        var first = ThreeFiles("run/a.cobertura.xml");
        var second = ThreeFiles("run/nested/b.cobertura.xml");

        var (code, stdout, stderr) = await Run("report", _ws.PathOf("run"), "--format", "json");

        await Assert.That(code).IsEqualTo(0);
        var inputs = Parse(stdout).GetProperty("inputs").EnumerateArray().Select(static i => i.GetString()!).ToArray();
        await Assert.That(inputs).IsEquivalentTo([first, second], CollectionOrdering.Matching);
        foreach (var input in inputs)
            await Assert.That(stderr).Contains($"  {input}");
    }

    [Test]
    public async Task Crap_Json_CarriesTheParserWarnings()
    {
        var report = _ws.Write("w.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/A.cs", "MyApp.A", c => c
                .Method("M", "()", "1", m => m.Line(1, hits: 1).MalformedLine("2", "lots"))));

        var (_, stdout, _) = await Run("crap", report, "--format", "json");

        var warning = Parse(stdout).GetProperty("warnings").EnumerateArray().Single();
        await Assert.That(warning.GetProperty("kind").GetString()).IsEqualTo("MalformedHits");
        await Assert.That(warning.GetProperty("file").GetString()).IsEqualTo("src/A.cs");
    }

    // ── Writing: relaxed escaping and a trailing newline ──

    [Test]
    [Arguments("report")]
    [Arguments("check")]
    [Arguments("crap")]
    [Arguments("diff")]
    [Arguments("snapshot")]
    public async Task Json_KeepsPathsReadable_AndEndsWithANewline(string command)
    {
        const string path = "src/Größe<T>&'Ü'.cs";
        var report = _ws.Write("escape.cobertura.xml", Cobertura.NewDoc()
            .AddClass(path, "MyApp.A", c => c.Method("M", "()", "1", m => m.Line(1, hits: 1))));
        string[] args = command switch
        {
            "check" => ["check", report, "--min-line", "50", "--format", "json"],
            "diff" => ["diff", report, report, "--format", "json"],
            "snapshot" => ["snapshot", report, "--commit", "c", "--branch", "b", "--project", "p"],
            _ => [command, report, "--format", "json"]
        };

        var (_, stdout, _) = await Run(args);

        await Assert.That(stdout).Contains($"\"{path}\"");
        await Assert.That(stdout).EndsWith("}" + Environment.NewLine);
    }

    // ── report: uncovered ranges and --top ──

    [Test]
    public async Task Report_Json_SpellsUncoveredLinesAsRanges()
    {
        var (_, stdout, _) = await Run("report", ThreeFiles(), "--format", "json");

        var files = Parse(stdout).GetProperty("files").EnumerateArray().ToDictionary(static f => f.GetProperty("path").GetString()!);
        await Assert.That(files["src/A.cs"].GetProperty("uncovered").GetString()).IsEqualTo("21,23-24");
        await Assert.That(files["src/B.cs"].GetProperty("uncovered").GetString()).IsEqualTo("2");
        await Assert.That(files["src/C.cs"].TryGetProperty("uncovered", out _)).IsFalse();
        await Assert.That(files["src/A.cs"].TryGetProperty("uncoveredLines", out _)).IsFalse();
    }

    [Test]
    public async Task Report_Top_TableShowsTheWorstFiles_AndCountsTheRest()
    {
        var (code, stdout, _) = await Run("report", ThreeFiles(), "--top", "1");

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stdout).Contains("src/B.cs");
        await Assert.That(stdout).DoesNotContain("src/A.cs");
        await Assert.That(stdout).DoesNotContain("src/C.cs");
        await Assert.That(stdout).Contains("… 2 more files");
        // TOTAL still counts every file.
        await Assert.That(stdout).Contains("8/12");
    }

    [Test]
    public async Task Report_Top_MarkdownShowsTheWorstFiles_AndCountsTheRest()
    {
        var (_, stdout, _) = await Run("report", ThreeFiles(), "--top", "2", "--format", "md");

        await Assert.That(stdout).Contains("`src/B.cs`");
        await Assert.That(stdout).Contains("`src/A.cs`");
        await Assert.That(stdout).DoesNotContain("`src/C.cs`");
        await Assert.That(stdout).Contains("_… 1 more files_");
    }

    [Test]
    public async Task Report_Top_JsonKeepsTheWorstFilesWorstFirst_AndCountsTheRest()
    {
        var (_, stdout, _) = await Run("report", ThreeFiles(), "--top", "2", "--format", "json");

        var root = Parse(stdout);
        await Assert.That(root.GetProperty("files").EnumerateArray().Select(static f => f.GetProperty("path").GetString()!))
            .IsEquivalentTo(["src/B.cs", "src/A.cs"], CollectionOrdering.Matching);
        await Assert.That(root.GetProperty("moreFiles").GetInt32()).IsEqualTo(1);
        await Assert.That(root.GetProperty("summary").GetProperty("totalLines").GetInt32()).IsEqualTo(12);
    }

    [Test]
    [Arguments("table")]
    [Arguments("md")]
    public async Task Report_TopCoveringEveryFile_ShowsThemAll_WithoutAMoreLine(string format)
    {
        var (_, stdout, _) = await Run("report", ThreeFiles(), "--top", "3", "--format", format);

        foreach (var file in new[] { "src/A.cs", "src/B.cs", "src/C.cs" })
            await Assert.That(stdout).Contains(file);
        await Assert.That(stdout).DoesNotContain("more files");
    }

    [Test]
    public async Task Report_WithoutTop_KeepsDocumentOrder_AndHasNoMoreFiles()
    {
        var (_, stdout, _) = await Run("report", ThreeFiles(), "--format", "json");

        var root = Parse(stdout);
        await Assert.That(root.GetProperty("files").EnumerateArray().Select(static f => f.GetProperty("path").GetString()!))
            .IsEquivalentTo(["src/C.cs", "src/A.cs", "src/B.cs"], CollectionOrdering.Matching);
        await Assert.That(root.TryGetProperty("moreFiles", out _)).IsFalse();
    }

    // ── check --format json: the report plus the gate ──

    [Test]
    public async Task Check_Json_Fail_CarriesTheGate_AndTheReport()
    {
        var path = ThreeFiles();

        var (code, stdout, stderr) = await Run("check", path, "--min-line", "80", "--min-branch", "60", "--format", "json");

        await Assert.That(code).IsEqualTo(1);
        var root = Parse(stdout);
        await Assert.That(Keys(root)).IsEquivalentTo(["schemaVersion", "command", "inputs", "warnings", "gate", "summary", "files"], CollectionOrdering.Matching);
        var gate = root.GetProperty("gate");
        await Assert.That(gate.GetProperty("outcome").GetString()).IsEqualTo("fail");
        await Assert.That(gate.GetProperty("exitCode").GetInt32()).IsEqualTo(1);
        // The verdict line, exactly as stderr leads with it.
        var verdict = stderr.Split(Environment.NewLine)[0];
        await Assert.That(verdict).IsEqualTo("FAIL: line 66.6% (min 80%), branch 25.0% (min 60%) - line and branch coverage below threshold");
        await Assert.That(gate.GetProperty("reason").GetString()).IsEqualTo(verdict);
        // A failing rate floors, so it never reads as the minimum it missed.
        await Assert.That(gate.GetProperty("line").GetProperty("rate").GetDouble()).IsEqualTo(66.66);
        await Assert.That(gate.GetProperty("line").GetProperty("min").GetDouble()).IsEqualTo(80);
        await Assert.That(gate.GetProperty("branch").GetProperty("rate").GetDouble()).IsEqualTo(25);
        await Assert.That(gate.GetProperty("branch").GetProperty("min").GetDouble()).IsEqualTo(60);
        await Assert.That(gate.GetProperty("offenders").GetArrayLength()).IsEqualTo(3);
        await Assert.That(gate.GetProperty("moreOffenders").GetInt32()).IsEqualTo(0);
        // The JSON carries the offenders; stderr keeps the verdict and diagnostics only.
        await Assert.That(stderr).DoesNotContain("files below");
        await Assert.That(root.GetProperty("summary").GetProperty("coveredLines").GetInt32()).IsEqualTo(8);
    }

    [Test]
    public async Task Check_Json_Pass_IsTheWholeStdout()
    {
        var (code, stdout, _) = await Run("check", ThreeFiles(), "--min-line", "60", "--format", "json");

        await Assert.That(code).IsEqualTo(0);
        var gate = Parse(stdout).GetProperty("gate");   // throws if a verdict line followed the document
        await Assert.That(gate.GetProperty("outcome").GetString()).IsEqualTo("pass");
        await Assert.That(gate.GetProperty("exitCode").GetInt32()).IsEqualTo(0);
        await Assert.That(gate.GetProperty("reason").GetString()).StartsWith("PASS: line 66.7% (min 60%)");
        await Assert.That(gate.GetProperty("line").GetProperty("rate").GetDouble()).IsEqualTo(66.67);
        // The branch dimension was not asked about.
        await Assert.That(gate.GetProperty("branch").GetProperty("min").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(gate.GetProperty("offenders").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task Check_Json_NoData_HasNullRates_AndExitCode3()
    {
        var empty = _ws.CreateDirectory("empty");

        var (code, stdout, stderr) = await Run("check", empty, "--min-line", "80", "--format", "json");

        await Assert.That(code).IsEqualTo(DotCovCli.NoDataExitCode);
        await Assert.That(stderr).StartsWith("NODATA:");
        var root = Parse(stdout);
        var gate = root.GetProperty("gate");
        await Assert.That(gate.GetProperty("outcome").GetString()).IsEqualTo("nodata");
        await Assert.That(gate.GetProperty("exitCode").GetInt32()).IsEqualTo(3);
        await Assert.That(gate.GetProperty("line").GetProperty("rate").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(root.GetProperty("inputs").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task Check_Json_Disabled_HasNullMinimums_AndExitCode4()
    {
        var (code, stdout, _) = await Run("check", ThreeFiles(), "--min-line", "0", "--min-branch", "0", "--format", "json");

        await Assert.That(code).IsEqualTo(DotCovCli.DisabledExitCode);
        var gate = Parse(stdout).GetProperty("gate");
        await Assert.That(gate.GetProperty("outcome").GetString()).IsEqualTo("disabled");
        await Assert.That(gate.GetProperty("exitCode").GetInt32()).IsEqualTo(4);
        await Assert.That(gate.GetProperty("line").GetProperty("min").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(gate.GetProperty("branch").GetProperty("min").ValueKind).IsEqualTo(JsonValueKind.Null);
        await Assert.That(gate.GetProperty("line").GetProperty("rate").GetDouble()).IsEqualTo(66.67);
    }

    [Test]
    [Arguments("md")]
    [Arguments("markdown")]
    public async Task Check_Markdown_PrintsTheSummaryWithItsOffenders(string format)
    {
        var (code, stdout, stderr) = await Run("check", ThreeFiles(), "--min-line", "80", "--format", format);

        await Assert.That(code).IsEqualTo(1);
        await Assert.That(stdout).StartsWith("## Coverage Report ❌");
        await Assert.That(stdout).Contains("### Files below line threshold" + Environment.NewLine + Environment.NewLine +
            "- `src/B.cs` — 50.0% lines — uncovered 2" + Environment.NewLine +
            "- `src/A.cs` — 57.1% lines, 16.7% branches — uncovered 21,23-24 · branches 20 (1/2), 26 (0/4)" + Environment.NewLine);
        await Assert.That(stdout).DoesNotContain("more files");
        await Assert.That(stdout).Contains("`FAIL: line 66.6% (min 80%)");
        await Assert.That(stderr).StartsWith("FAIL: line 66.6% (min 80%)");
        await Assert.That(stderr).DoesNotContain("files below");
    }

    [Test]
    public async Task Check_Markdown_Top_CapsTheOffenders_AndCountsTheRest()
    {
        var (_, stdout, _) = await Run("check", ThreeFiles(), "--min-line", "80", "--format", "md", "--top", "1");

        await Assert.That(stdout).Contains("- `src/B.cs` — 50.0% lines — uncovered 2" + Environment.NewLine +
            Environment.NewLine + "_… 1 more files_" + Environment.NewLine);
        await Assert.That(stdout).DoesNotContain("- `src/A.cs`");
    }

    [Test]
    public async Task Check_Markdown_Pass_StdoutIsTheSummary_TheVerdictLeadsStderr()
    {
        var (code, stdout, stderr) = await Run("check", ThreeFiles(), "--min-line", "60", "--format", "md");

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stdout).StartsWith("## Coverage Report ✅");
        await Assert.That(stdout).DoesNotContain("Files below");
        await Assert.That(stderr).StartsWith("PASS: line 66.7% (min 60%), branch 25.0% (min 0%) - thresholds met" + Environment.NewLine);
    }

    [Test]
    [Arguments("check", "--format", "xml", "Invalid --format value: 'xml'")]
    [Arguments("check", "--top", "0", "Invalid --top value: '0'")]
    [Arguments("report", "--top", "x", "Invalid --top value: 'x'")]
    // test checks its flags before it creates a results directory or starts dotnet test.
    [Arguments("test", "--format", "xml", "Invalid --format value: 'xml'")]
    public async Task InvalidFormatOrTop_IsAUsageError(string command, string flag, string value, string expected)
    {
        string[] args = command switch
        {
            "check" => ["check", ThreeFiles(), "--min-line", "80", flag, value],
            "test" => ["test", "--min-line", "80", flag, value],
            _ => [command, ThreeFiles(), flag, value]
        };

        var (code, stdout, stderr) = await Run(args);

        await Assert.That(code).IsEqualTo(DotCovCli.UsageExitCode);
        await Assert.That(stderr).Contains(expected);
        await Assert.That(stdout).IsEmpty();
    }
}
