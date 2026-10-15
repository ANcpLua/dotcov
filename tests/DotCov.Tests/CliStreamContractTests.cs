using System.Text.Json;
using DotCov.Tests.Infrastructure;
using DotCov.Tool;

namespace DotCov.Tests;
/// <summary>
/// One predictable stream contract, through <see cref = "DotCovCli.RunAsync"/>: stdout carries only
/// a command's data, the gate verdict is the first stderr line of check and crap in every format
/// and outcome, a directory that matched no report says so, and a diff lists what moved.
/// `dotcov test` is pinned through its process in <see cref = "CliProcessContractTests"/>.
/// </summary>
[TestClass]
public sealed class CliStreamContractTests : IDisposable
{
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-streams-");
    public void Dispose() => _ws.Dispose();
    private static async Task<(int Code, string StdOut, string StdErr)> Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await DotCovCli.RunAsync(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private static string[] Lines(string text) => text.Split(Environment.NewLine);
    /// <summary>50% of lines, one method of complexity 2 (CRAP 2.5), no branch data.</summary>
    private string HalfCovered(string relative = "half.cobertura.xml") => _ws.Write(relative, Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("M", "()", "2", m => m.Line(1, hits: 1).Line(2, hits: 0))));
    // ── The verdict leads stderr; stdout holds only data ──
    [TestMethod]
    [DataRow("40", 0, "PASS: ")]
    [DataRow("90", 1, "FAIL: ")]
    [DataRow("0", 4, "DISABLED: ")]
    public async Task Check_EveryOutcome_VerdictFirstOnStderr_NothingOnStdout(string minLine, int expectedCode, string verdict)
    {
        var(code, stdout, stderr) = await Run("check", HalfCovered(), "--min-line", minLine);
        Assert.AreEqual(expectedCode, code);
        Assert.IsEmpty(stdout);
        Assert.StartsWith(verdict, stderr);
    }

    [TestMethod]
    [CombinatorialData]
    public async Task Crap_EveryFormatAndOutcome_VerdictFirstOnStderr_NotAsALineOfStdout([CombinatorialValues("table", "md", "json")] string format, [CombinatorialValues("30", "2")] string maxCrap)
    {
        var(code, stdout, stderr) = await Run("crap", HalfCovered(), "--max-crap", maxCrap, "--format", format);
        var verdict = Lines(stderr)[0];
        Assert.AreEqual(maxCrap is "30" ? 0 : 1, code);
        Assert.StartsWith(maxCrap is "30" ? "PASS: worst CRAP 2.5 (max 30)" : "FAIL: worst CRAP 2.5 (max 2)", verdict);
        // Markdown renders the verdict inside its report (in backticks); no format adds it as a bare line.
        Assert.DoesNotContain(verdict, Lines(stdout));
        if (format is "json")
            Assert.IsNotNull(JsonDocument.Parse(stdout).RootElement.GetProperty("gate").GetProperty("reason").GetString());
    }

    [TestMethod]
    [DataRow("table")]
    [DataRow("md")]
    [DataRow("json")]
    public async Task Report_HasNoVerdict_StdoutIsTheReport_StderrStaysEmpty(string format)
    {
        // --threshold is presentation only: the markdown shows it; no gate line is written.
        var(code, stdout, stderr) = await Run("report", HalfCovered(), "--format", format, "--threshold", "90");
        Assert.AreEqual(0, code);
        Assert.Contains("src/A.cs", stdout);
        Assert.IsEmpty(stderr);
    }

    // ── A directory that matched nothing says so, after any verdict ──
    [TestMethod]
    public async Task Check_EmptyDirectory_NoDataFirst_ThenTheWarningNamingPatternAndDirectory()
    {
        var empty = _ws.CreateDirectory("empty");
        var(code, stdout, stderr) = await Run("check", empty, "--min-line", "80");
        Assert.AreEqual(3, code);
        Assert.IsEmpty(stdout);
        var lines = Lines(stderr);
        Assert.StartsWith("NODATA: ", lines[0]);
        Assert.AreEqual($"warning: no file in '{empty}' matches '**/*cobertura*.xml'", lines[1]);
        Assert.HasCount(3, lines);
    }

    [TestMethod]
    public async Task Report_DirectoryWithOtherXml_NamesThreeOfThem_AndThePatternThatReadsTheFirst()
    {
        var dir = _ws.CreateDirectory("gcovr");
        HalfCovered("gcovr/b/coverage.xml");
        HalfCovered("gcovr/a/coverage.xml");
        _ws.Write("gcovr/c.XML", "<other/>");
        _ws.Write("gcovr/d/report.xml", "<other/>");
        _ws.Write("gcovr/notes.txt", "not xml");
        var(code, stdout, stderr) = await Run("report", dir);
        // Behaviour is otherwise unchanged: the (empty) report still prints.
        Assert.AreEqual(0, code);
        Assert.Contains("TOTAL", stdout);
        var a = Path.Combine("a", "coverage.xml");
        var b = Path.Combine("b", "coverage.xml");
        Assert.AreEqual($"warning: no file in '{dir}' matches '**/*cobertura*.xml'; .xml files there: {a}, {b}, c.XML (+1 more); " + $"try --pattern \"**/coverage.xml\"{Environment.NewLine}", stderr);
        // The advice, followed, reads the reports.
        var(adviceCode, adviceOut, adviceErr) = await Run("report", dir, "--pattern", "**/coverage.xml");
        Assert.AreEqual(0, adviceCode);
        Assert.Contains("src/A.cs", adviceOut);
        Assert.DoesNotContain("warning: no file", adviceErr);
    }

    [TestMethod]
    public async Task TopLevelPattern_MissingANestedReport_IsPointedAtTheRecursiveForm()
    {
        var dir = _ws.CreateDirectory("nested");
        HalfCovered("nested/run/coverage.xml");
        var(code, _, stderr) = await Run("check", dir, "--min-line", "40", "--pattern", "coverage.xml");
        Assert.AreEqual(3, code);
        Assert.AreEqual($"warning: no file in '{dir}' matches 'coverage.xml'; .xml files there: {Path.Combine("run", "coverage.xml")}; " + "try --pattern \"**/coverage.xml\"", Lines(stderr)[1]);
    }

    [TestMethod]
    [DataRow("crap")]
    [DataRow("snapshot")]
    public async Task EveryReportReadingCommand_WarnsAboutAnEmptyDirectory(string command)
    {
        var empty = _ws.CreateDirectory("empty");
        var(_, _, stderr) = await Run(command, empty);
        Assert.Contains($"warning: no file in '{empty}' matches '**/*cobertura*.xml'{Environment.NewLine}", stderr);
    }

    [TestMethod]
    public async Task Diff_EmptyBeforeDirectory_IsNamed_TheAfterFileIsNot()
    {
        var empty = _ws.CreateDirectory("before");
        var after = HalfCovered();
        var(code, stdout, stderr) = await Run("diff", empty, after);
        Assert.AreEqual(0, code);
        Assert.Contains("src/A.cs", stdout);
        Assert.AreEqual($"warning: no file in '{empty}' matches '**/*cobertura*.xml'{Environment.NewLine}", stderr);
    }

    [TestMethod]
    public async Task FileArgument_NeverWarns()
    {
        var(_, _, stderr) = await Run("report", HalfCovered());
        Assert.IsEmpty(stderr);
    }

    // ── A diff lists what moved ──
    [TestMethod]
    [DataRow("table", "Unchanged: 2 files")]
    [DataRow("md", "**Unchanged:** 2 files")]
    public async Task Diff_ListsOnlyTheFilesThatMoved_AndCountsTheRest(string format, string count)
    {
        var before = _ws.Write("before.cobertura.xml", Cobertura.NewDoc().AddClass("src/Moved.cs", c => c.Line(1, hits: 1).Line(2, hits: 0)).AddClass("src/Still.cs", c => c.Line(1, hits: 1)).AddClass("src/AlsoStill.cs", c => c.Line(1, hits: 0)).AddClass("src/Gone.cs", c => c.Line(1, hits: 1)));
        var after = _ws.Write("after.cobertura.xml", Cobertura.NewDoc().AddClass("src/Moved.cs", c => c.Line(1, hits: 1).Line(2, hits: 1)).AddClass("src/Still.cs", c => c.Line(1, hits: 1)).AddClass("src/AlsoStill.cs", c => c.Line(1, hits: 0)).AddClass("src/New.cs", c => c.Line(1, hits: 1)));
        var(code, stdout, stderr) = await Run("diff", before, after, "--format", format);
        Assert.AreEqual(0, code, message: stderr);
        Assert.Contains("src/Moved.cs", stdout);
        Assert.Contains("src/Gone.cs", stdout);
        Assert.Contains("src/New.cs", stdout);
        Assert.DoesNotContain("Still.cs", stdout);
        Assert.Contains(count + Environment.NewLine, stdout);
    }

    [TestMethod]
    [DataRow("table")]
    [DataRow("json")]
    public async Task Diff_AppliesTheExclusionsToBothSides_AndNamesThem(string format)
    {
        var before = _ws.Write("before.cobertura.xml", Cobertura.NewDoc().AddClass("src/A.cs", c => c.Line(1, hits: 0)).AddClass("obj/Gen.g.cs", c => c.Line(1, hits: 0)));
        var after = _ws.Write("after.cobertura.xml", Cobertura.NewDoc().AddClass("src/A.cs", c => c.Line(1, hits: 1)).AddClass("obj/Gen.g.cs", c => c.Line(1, hits: 1)));
        var(code, stdout, stderr) = await Run("diff", before, after, "--format", format, "--exclude-generated");
        Assert.AreEqual(0, code, message: stderr);
        Assert.Contains("src/A.cs", stdout);
        if (format is "json")
        {
            using var json = JsonDocument.Parse(stdout);
            Assert.AreEqual(1, json.RootElement.GetProperty("files").GetArrayLength());
            Assert.AreSequenceEqual(["obj/Gen.g.cs"], json.RootElement.GetProperty("excluded").EnumerateArray().Select(static e => e.GetString()!), SequenceOrder.InAnyOrder);
            Assert.DoesNotContain("obj/Gen.g.cs", stderr);
        }
        else
        {
            Assert.DoesNotContain("Gen.g.cs", stdout);
            Assert.Contains("obj/Gen.g.cs", stderr);
        }
    }
}
