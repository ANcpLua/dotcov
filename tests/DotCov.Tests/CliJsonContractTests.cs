using System.Text.Json;
using DotCov.Tests.Infrastructure;
using DotCov.Tool;

namespace DotCov.Tests;
/// <summary>
/// The JSON contract every command shares, pinned through <see cref = "DotCovCli.RunAsync"/>: each
/// document opens with <c>schemaVersion</c> and <c>command</c>, names the reports it merged in
/// <c>inputs</c>, carries the parser's <c>warnings</c>, escapes nothing a JSON parser does not
/// need escaped, and ends with a newline. check takes <c>--format json</c> and prints the report
/// document plus a <c>gate</c> object that answers what to fix next.
/// </summary>
[TestClass]
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
    private string ThreeFiles(string relative = "three.cobertura.xml") => _ws.Write(relative, Cobertura.NewDoc().AddClass("src/C.cs", c => c.Line(1, hits: 1).Line(2, hits: 1).Branch(3, "50% (1/2)")).AddClass("src/A.cs", c => c.Line(19, hits: 1).Branch(20, "50% (1/2)").Line(21, hits: 0).Line(22, hits: 1).Line(23, hits: 0).Line(24, hits: 0).Branch(26, "0% (0/4)")).AddClass("src/B.cs", c => c.Line(1, hits: 1).Line(2, hits: 0)));
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;
    private static string[] Keys(JsonElement element) => [..element.EnumerateObject().Select(static p => p.Name)];
    // ── Every document opens with schemaVersion and command ──
    [TestMethod]
    [DataRow("report")]
    [DataRow("check")]
    [DataRow("crap")]
    [DataRow("snapshot")]
    public async Task EveryJsonDocument_OpensWithSchemaVersionAndCommand(string command)
    {
        var report = _ws.Write("m.cobertura.xml", Cobertura.NewDoc().AddClass("src/A.cs", "MyApp.A", c => c.Method("M", "()", "1", m => m.Line(1, hits: 1))));
        string[] args = command switch
        {
            "check" => ["check", report, "--min-line", "50", "--format", "json"],
            "snapshot" => ["snapshot", report, "--commit", "c", "--branch", "b", "--project", "p"],
            _ => [command, report, "--format", "json"]
        };
        var(code, stdout, stderr) = await Run(args);
        Assert.AreEqual(0, code, message: stderr);
        var root = Parse(stdout);
        Assert.AreSequenceEqual(["schemaVersion", "command", "inputs", "warnings"], Keys(root).Take(4), SequenceOrder.InOrder);
        Assert.AreEqual(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual(command, root.GetProperty("command").GetString());
        Assert.AreSequenceEqual([report], root.GetProperty("inputs").EnumerateArray().Select(static i => i.GetString()!), SequenceOrder.InOrder);
        Assert.AreEqual(0, root.GetProperty("warnings").GetArrayLength());
    }

    [TestMethod]
    public async Task Diff_Json_ListsTheInputsOfBothSides_AndTheirWarnings()
    {
        var before = _ws.Write("before.xml", Cobertura.NewDoc().AddClass("src/A.cs", c => c.MalformedLine("1", "lots").Line(2, hits: 1)));
        var after = _ws.Write("after.xml", Cobertura.NewDoc().AddClass("src/A.cs", c => c.Line(2, hits: 1)));
        var(code, stdout, _) = await Run("diff", before, after, "--format", "json");
        Assert.AreEqual(0, code);
        var root = Parse(stdout);
        Assert.AreSequenceEqual(["schemaVersion", "command", "inputs", "warnings"], Keys(root).Take(4), SequenceOrder.InOrder);
        Assert.AreEqual("diff", root.GetProperty("command").GetString());
        Assert.AreSequenceEqual([before, after], root.GetProperty("inputs").EnumerateArray().Select(static i => i.GetString()!), SequenceOrder.InOrder);
        var warning = root.GetProperty("warnings").EnumerateArray().Single();
        Assert.AreEqual("MalformedHits", warning.GetProperty("kind").GetString());
        var detail = warning.GetProperty("detail").GetString();
        Assert.IsNotNull(detail);
        Assert.Contains("lots", detail);
    }

    [TestMethod]
    public async Task Inputs_NameEveryMergedReport_AsStderrDoes()
    {
        var first = ThreeFiles("run/a.cobertura.xml");
        var second = ThreeFiles("run/nested/b.cobertura.xml");
        var(code, stdout, stderr) = await Run("report", _ws.PathOf("run"), "--format", "json");
        Assert.AreEqual(0, code);
        var inputs = Parse(stdout).GetProperty("inputs").EnumerateArray().Select(static i => i.GetString()!).ToArray();
        Assert.AreSequenceEqual([first, second], inputs, SequenceOrder.InOrder);
        foreach (var input in inputs)
            Assert.Contains($"  {input}", stderr);
    }

    [TestMethod]
    public async Task Crap_Json_CarriesTheParserWarnings()
    {
        var report = _ws.Write("w.cobertura.xml", Cobertura.NewDoc().AddClass("src/A.cs", "MyApp.A", c => c.Method("M", "()", "1", m => m.Line(1, hits: 1).MalformedLine("2", "lots"))));
        var(_, stdout, _) = await Run("crap", report, "--format", "json");
        var warning = Parse(stdout).GetProperty("warnings").EnumerateArray().Single();
        Assert.AreEqual("MalformedHits", warning.GetProperty("kind").GetString());
        Assert.AreEqual("src/A.cs", warning.GetProperty("file").GetString());
    }

    // ── Writing: relaxed escaping and a trailing newline ──
    [TestMethod]
    [DataRow("report")]
    [DataRow("check")]
    [DataRow("crap")]
    [DataRow("diff")]
    [DataRow("snapshot")]
    public async Task Json_KeepsPathsReadable_AndEndsWithANewline(string command)
    {
        const string path = "src/Größe<T>&'Ü'.cs";
        var report = _ws.Write("escape.cobertura.xml", Cobertura.NewDoc().AddClass(path, "MyApp.A", c => c.Method("M", "()", "1", m => m.Line(1, hits: 1))));
        string[] args = command switch
        {
            "check" => ["check", report, "--min-line", "50", "--format", "json"],
            "diff" => ["diff", report, report, "--format", "json"],
            "snapshot" => ["snapshot", report, "--commit", "c", "--branch", "b", "--project", "p"],
            _ => [command, report, "--format", "json"]
        };
        var(_, stdout, _) = await Run(args);
        Assert.Contains($"\"{path}\"", stdout);
        Assert.EndsWith("}" + Environment.NewLine, stdout);
    }

    // ── report: uncovered ranges and --top ──
    [TestMethod]
    public async Task Report_Json_SpellsUncoveredLinesAsRanges()
    {
        var(_, stdout, _) = await Run("report", ThreeFiles(), "--format", "json");
        var files = Parse(stdout).GetProperty("files").EnumerateArray().ToDictionary(static f => f.GetProperty("path").GetString()!);
        Assert.AreEqual("21,23-24", files["src/A.cs"].GetProperty("uncovered").GetString());
        Assert.AreEqual("2", files["src/B.cs"].GetProperty("uncovered").GetString());
        Assert.IsFalse(files["src/C.cs"].TryGetProperty("uncovered", out _));
        Assert.IsFalse(files["src/A.cs"].TryGetProperty("uncoveredLines", out _));
    }

    [TestMethod]
    public async Task Report_Top_TableShowsTheWorstFiles_AndCountsTheRest()
    {
        var(code, stdout, _) = await Run("report", ThreeFiles(), "--top", "1");
        Assert.AreEqual(0, code);
        Assert.Contains("src/B.cs", stdout);
        Assert.DoesNotContain("src/A.cs", stdout);
        Assert.DoesNotContain("src/C.cs", stdout);
        Assert.Contains("… 2 more files", stdout);
        // TOTAL still counts every file.
        Assert.Contains("8/12", stdout);
    }

    [TestMethod]
    public async Task Report_Top_MarkdownShowsTheWorstFiles_AndCountsTheRest()
    {
        var(_, stdout, _) = await Run("report", ThreeFiles(), "--top", "2", "--format", "md");
        Assert.Contains("`src/B.cs`", stdout);
        Assert.Contains("`src/A.cs`", stdout);
        Assert.DoesNotContain("`src/C.cs`", stdout);
        Assert.Contains("_… 1 more files_", stdout);
    }

    [TestMethod]
    public async Task Report_Top_JsonKeepsTheWorstFilesWorstFirst_AndCountsTheRest()
    {
        var(_, stdout, _) = await Run("report", ThreeFiles(), "--top", "2", "--format", "json");
        var root = Parse(stdout);
        Assert.AreSequenceEqual(["src/B.cs", "src/A.cs"], root.GetProperty("files").EnumerateArray().Select(static f => f.GetProperty("path").GetString()!), SequenceOrder.InOrder);
        Assert.AreEqual(1, root.GetProperty("moreFiles").GetInt32());
        Assert.AreEqual(12, root.GetProperty("summary").GetProperty("totalLines").GetInt32());
    }

    [TestMethod]
    [DataRow("table")]
    [DataRow("md")]
    public async Task Report_TopCoveringEveryFile_ShowsThemAll_WithoutAMoreLine(string format)
    {
        var(_, stdout, _) = await Run("report", ThreeFiles(), "--top", "3", "--format", format);
        foreach (var file in new[]
        {
            "src/A.cs",
            "src/B.cs",
            "src/C.cs"
        }

        )
            Assert.Contains(file, stdout);
        Assert.DoesNotContain("more files", stdout);
    }

    [TestMethod]
    public async Task Report_WithoutTop_KeepsDocumentOrder_AndHasNoMoreFiles()
    {
        var(_, stdout, _) = await Run("report", ThreeFiles(), "--format", "json");
        var root = Parse(stdout);
        Assert.AreSequenceEqual(["src/C.cs", "src/A.cs", "src/B.cs"], root.GetProperty("files").EnumerateArray().Select(static f => f.GetProperty("path").GetString()!), SequenceOrder.InOrder);
        Assert.IsFalse(root.TryGetProperty("moreFiles", out _));
    }

    // ── check --format json: the report plus the gate ──
    [TestMethod]
    public async Task Check_Json_Fail_CarriesTheGate_AndTheReport()
    {
        var path = ThreeFiles();
        var(code, stdout, stderr) = await Run("check", path, "--min-line", "80", "--min-branch", "60", "--format", "json");
        Assert.AreEqual(1, code);
        var root = Parse(stdout);
        Assert.AreSequenceEqual(["schemaVersion", "command", "inputs", "warnings", "gate", "summary", "files"], Keys(root), SequenceOrder.InOrder);
        var gate = root.GetProperty("gate");
        Assert.AreEqual("fail", gate.GetProperty("outcome").GetString());
        Assert.AreEqual(1, gate.GetProperty("exitCode").GetInt32());
        // The verdict line, exactly as stderr leads with it.
        var verdict = stderr.Split(Environment.NewLine)[0];
        Assert.AreEqual("FAIL: line 66.6% (min 80%), branch 25.0% (min 60%) - line and branch coverage below threshold", verdict);
        Assert.AreEqual(verdict, gate.GetProperty("reason").GetString());
        // A failing rate floors, so it never reads as the minimum it missed.
        Assert.AreEqual(66.66, gate.GetProperty("line").GetProperty("rate").GetDouble());
        Assert.AreEqual(80, gate.GetProperty("line").GetProperty("min").GetDouble());
        Assert.AreEqual(25, gate.GetProperty("branch").GetProperty("rate").GetDouble());
        Assert.AreEqual(60, gate.GetProperty("branch").GetProperty("min").GetDouble());
        Assert.AreEqual(3, gate.GetProperty("offenders").GetArrayLength());
        Assert.AreEqual(0, gate.GetProperty("moreOffenders").GetInt32());
        // The JSON carries the offenders; stderr keeps the verdict and diagnostics only.
        Assert.DoesNotContain("files below", stderr);
        Assert.AreEqual(8, root.GetProperty("summary").GetProperty("coveredLines").GetInt32());
    }

    [TestMethod]
    public async Task Check_Json_Pass_IsTheWholeStdout()
    {
        var(code, stdout, _) = await Run("check", ThreeFiles(), "--min-line", "60", "--format", "json");
        Assert.AreEqual(0, code);
        var gate = Parse(stdout).GetProperty("gate"); // throws if a verdict line followed the document
        Assert.AreEqual("pass", gate.GetProperty("outcome").GetString());
        Assert.AreEqual(0, gate.GetProperty("exitCode").GetInt32());
        Assert.StartsWith("PASS: line 66.7% (min 60%)", gate.GetProperty("reason").GetString());
        Assert.AreEqual(66.67, gate.GetProperty("line").GetProperty("rate").GetDouble());
        // The branch dimension was not asked about.
        Assert.AreEqual(JsonValueKind.Null, gate.GetProperty("branch").GetProperty("min").ValueKind);
        Assert.AreEqual(0, gate.GetProperty("offenders").GetArrayLength());
    }

    [TestMethod]
    public async Task Check_Json_NoData_HasNullRates_AndExitCode3()
    {
        var empty = _ws.CreateDirectory("empty");
        var(code, stdout, stderr) = await Run("check", empty, "--min-line", "80", "--format", "json");
        Assert.AreEqual(DotCovCli.NoDataExitCode, code);
        Assert.StartsWith("NODATA:", stderr);
        var root = Parse(stdout);
        var gate = root.GetProperty("gate");
        Assert.AreEqual("nodata", gate.GetProperty("outcome").GetString());
        Assert.AreEqual(3, gate.GetProperty("exitCode").GetInt32());
        Assert.AreEqual(JsonValueKind.Null, gate.GetProperty("line").GetProperty("rate").ValueKind);
        Assert.AreEqual(0, root.GetProperty("inputs").GetArrayLength());
    }

    [TestMethod]
    public async Task Check_Json_Disabled_HasNullMinimums_AndExitCode4()
    {
        var(code, stdout, _) = await Run("check", ThreeFiles(), "--min-line", "0", "--min-branch", "0", "--format", "json");
        Assert.AreEqual(DotCovCli.DisabledExitCode, code);
        var gate = Parse(stdout).GetProperty("gate");
        Assert.AreEqual("disabled", gate.GetProperty("outcome").GetString());
        Assert.AreEqual(4, gate.GetProperty("exitCode").GetInt32());
        Assert.AreEqual(JsonValueKind.Null, gate.GetProperty("line").GetProperty("min").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, gate.GetProperty("branch").GetProperty("min").ValueKind);
        Assert.AreEqual(66.67, gate.GetProperty("line").GetProperty("rate").GetDouble());
    }

    [TestMethod]
    [DataRow("md")]
    [DataRow("markdown")]
    public async Task Check_Markdown_PrintsTheSummaryWithItsOffenders(string format)
    {
        var(code, stdout, stderr) = await Run("check", ThreeFiles(), "--min-line", "80", "--format", format);
        Assert.AreEqual(1, code);
        Assert.StartsWith("## Coverage Report ❌", stdout);
        Assert.Contains("### Files below line threshold" + Environment.NewLine + Environment.NewLine + "- `src/B.cs` — 50.0% lines — uncovered 2" + Environment.NewLine + "- `src/A.cs` — 57.1% lines, 16.7% branches — uncovered 21,23-24 · branches 20 (1/2), 26 (0/4)" + Environment.NewLine, stdout);
        Assert.DoesNotContain("more files", stdout);
        Assert.Contains("`FAIL: line 66.6% (min 80%)", stdout);
        Assert.StartsWith("FAIL: line 66.6% (min 80%)", stderr);
        Assert.DoesNotContain("files below", stderr);
    }

    [TestMethod]
    public async Task Check_Markdown_Top_CapsTheOffenders_AndCountsTheRest()
    {
        var(_, stdout, _) = await Run("check", ThreeFiles(), "--min-line", "80", "--format", "md", "--top", "1");
        Assert.Contains("- `src/B.cs` — 50.0% lines — uncovered 2" + Environment.NewLine + Environment.NewLine + "_… 1 more files_" + Environment.NewLine, stdout);
        Assert.DoesNotContain("- `src/A.cs`", stdout);
    }

    [TestMethod]
    public async Task Check_Markdown_Pass_StdoutIsTheSummary_TheVerdictLeadsStderr()
    {
        var(code, stdout, stderr) = await Run("check", ThreeFiles(), "--min-line", "60", "--format", "md");
        Assert.AreEqual(0, code);
        Assert.StartsWith("## Coverage Report ✅", stdout);
        Assert.DoesNotContain("Files below", stdout);
        Assert.StartsWith("PASS: line 66.7% (min 60%), branch 25.0% (min 0%) - thresholds met" + Environment.NewLine, stderr);
    }

    [TestMethod]
    [DataRow("check", "--format", "xml", "Invalid --format value: 'xml'")]
    [DataRow("check", "--top", "0", "Invalid --top value: '0'")]
    [DataRow("report", "--top", "x", "Invalid --top value: 'x'")]
    // test checks its flags before it creates a results directory or starts dotnet test.
    [DataRow("test", "--format", "xml", "Invalid --format value: 'xml'")]
    public async Task InvalidFormatOrTop_IsAUsageError(string command, string flag, string value, string expected)
    {
        string[] args = command switch
        {
            "check" => ["check", ThreeFiles(), "--min-line", "80", flag, value],
            "test" => ["test", "--min-line", "80", flag, value],
            _ => [command, ThreeFiles(), flag, value]
        };
        var(code, stdout, stderr) = await Run(args);
        Assert.AreEqual(DotCovCli.UsageExitCode, code);
        Assert.Contains(expected, stderr);
        Assert.IsEmpty(stdout);
    }

    // 19,999 of 25,000 lines is 79.996%: rounded it reads 80, the minimum it missed.
    [TestMethod]
    public async Task Check_Json_FailedRates_FloorEverywhere_PassingOnesRound()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcov-floor-{Guid.NewGuid():N}.xml");
        await File.WriteAllBytesAsync(path, Cobertura.NewDoc().AddClass("src/A.cs", c =>
        {
            for (var i = 1; i <= 25_000; i++)
                c.Line(i, hits: i <= 19_999 ? 1 : 0);
        }).ToBytes());
        try
        {
            var(failCode, failing, _) = await Run("check", path, "--min-line", "80", "--format", "json");
            var(passCode, passing, _) = await Run("check", path, "--min-line", "79", "--format", "json");
            Assert.AreEqual(1, failCode);
            using var fail = JsonDocument.Parse(failing);
            Assert.AreEqual(79.99, fail.RootElement.GetProperty("summary").GetProperty("lineRate").GetDouble());
            Assert.AreEqual(79.99, fail.RootElement.GetProperty("files")[0].GetProperty("lineRate").GetDouble());
            Assert.AreEqual(0, passCode);
            using var pass = JsonDocument.Parse(passing);
            Assert.AreEqual(80, pass.RootElement.GetProperty("summary").GetProperty("lineRate").GetDouble());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [TestMethod]
    public async Task Report_Top_SizesTheTableFromTheRowsItShows()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dotcov-width-{Guid.NewGuid():N}.xml");
        var hidden = "src/" + new string ('x', 400) + ".cs";
        await File.WriteAllBytesAsync(path, Cobertura.NewDoc().AddClass("src/A.cs", c => c.Line(1, hits: 0)).AddClass(hidden, c => c.Line(1, hits: 1)).ToBytes());
        try
        {
            var(code, stdout, _) = await Run("report", path, "--top", "1");
            Assert.AreEqual(0, code);
            Assert.DoesNotContain(hidden, stdout);
            Assert.IsLessThan(80, stdout.Split('\n').Max(static l => l.TrimEnd('\r').Length));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
