using System.Text.Json;
using DotCov.Tests.Infrastructure;
using DotCov.Tool;

namespace DotCov.Tests;

/// <summary>
/// One predictable stream contract, through <see cref="DotCovCli.RunAsync"/>: stdout carries only
/// a command's data, the gate verdict is the first stderr line of check and crap in every format
/// and outcome, a directory that matched no report says so, and a diff lists what moved.
/// `dotcov test` is pinned through its process in <see cref="CliProcessContractTests"/>.
/// </summary>
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
    private string HalfCovered(string relative = "half.cobertura.xml") =>
        _ws.Write(relative, Cobertura.NewDoc()
            .AddClass("src/A.cs", "App.A", c => c.Method("M", "()", "2", m => m.Line(1, hits: 1).Line(2, hits: 0))));

    // ── The verdict leads stderr; stdout holds only data ──

    [Test]
    [Arguments("40", 0, "PASS: ")]
    [Arguments("90", 1, "FAIL: ")]
    [Arguments("0", 4, "DISABLED: ")]
    public async Task Check_EveryOutcome_VerdictFirstOnStderr_NothingOnStdout(string minLine, int expectedCode, string verdict)
    {
        var (code, stdout, stderr) = await Run("check", HalfCovered(), "--min-line", minLine);

        await Assert.That(code).IsEqualTo(expectedCode);
        await Assert.That(stdout).IsEmpty();
        await Assert.That(stderr).StartsWith(verdict);
    }

    [Test]
    [MatrixDataSource]
    public async Task Crap_EveryFormatAndOutcome_VerdictFirstOnStderr_NotAsALineOfStdout(
        [Matrix("table", "md", "json")] string format,
        [Matrix("30", "2")] string maxCrap)
    {
        var (code, stdout, stderr) = await Run("crap", HalfCovered(), "--max-crap", maxCrap, "--format", format);

        var verdict = Lines(stderr)[0];
        await Assert.That(code).IsEqualTo(maxCrap is "30" ? 0 : 1);
        await Assert.That(verdict).StartsWith(maxCrap is "30" ? "PASS: worst CRAP 2.5 (max 30)" : "FAIL: worst CRAP 2.5 (max 2)");
        // Markdown renders the verdict inside its report (in backticks); no format adds it as a bare line.
        await Assert.That(Lines(stdout)).DoesNotContain(verdict);
        if (format is "json")
            await Assert.That(JsonDocument.Parse(stdout).RootElement.GetProperty("gate").GetProperty("reason").GetString())
                .IsNotNull();
    }

    [Test]
    [Arguments("table")]
    [Arguments("md")]
    [Arguments("json")]
    public async Task Report_HasNoVerdict_StdoutIsTheReport_StderrStaysEmpty(string format)
    {
        // --threshold is presentation only: the markdown shows it; no gate line is written.
        var (code, stdout, stderr) = await Run("report", HalfCovered(), "--format", format, "--threshold", "90");

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stdout).Contains("src/A.cs");
        await Assert.That(stderr).IsEmpty();
    }

    // ── A directory that matched nothing says so, after any verdict ──

    [Test]
    public async Task Check_EmptyDirectory_NoDataFirst_ThenTheWarningNamingPatternAndDirectory()
    {
        var empty = _ws.CreateDirectory("empty");

        var (code, stdout, stderr) = await Run("check", empty, "--min-line", "80");

        await Assert.That(code).IsEqualTo(3);
        await Assert.That(stdout).IsEmpty();
        var lines = Lines(stderr);
        await Assert.That(lines[0]).StartsWith("NODATA: ");
        await Assert.That(lines[1]).IsEqualTo($"warning: no file in '{empty}' matches '**/*cobertura*.xml'");
        await Assert.That(lines).Count().IsEqualTo(3);
    }

    [Test]
    public async Task Report_DirectoryWithOtherXml_NamesThreeOfThem_AndThePatternThatReadsTheFirst()
    {
        var dir = _ws.CreateDirectory("gcovr");
        HalfCovered("gcovr/b/coverage.xml");
        HalfCovered("gcovr/a/coverage.xml");
        _ws.Write("gcovr/c.XML", "<other/>");
        _ws.Write("gcovr/d/report.xml", "<other/>");
        _ws.Write("gcovr/notes.txt", "not xml");

        var (code, stdout, stderr) = await Run("report", dir);

        // Behaviour is otherwise unchanged: the (empty) report still prints.
        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stdout).Contains("TOTAL");
        var a = Path.Combine("a", "coverage.xml");
        var b = Path.Combine("b", "coverage.xml");
        await Assert.That(stderr).IsEqualTo(
            $"warning: no file in '{dir}' matches '**/*cobertura*.xml'; .xml files there: {a}, {b}, c.XML (+1 more); " +
            $"try --pattern \"**/coverage.xml\"{Environment.NewLine}");

        // The advice, followed, reads the reports.
        var (adviceCode, adviceOut, adviceErr) = await Run("report", dir, "--pattern", "**/coverage.xml");
        await Assert.That(adviceCode).IsEqualTo(0);
        await Assert.That(adviceOut).Contains("src/A.cs");
        await Assert.That(adviceErr).DoesNotContain("warning: no file");
    }

    [Test]
    public async Task TopLevelPattern_MissingANestedReport_IsPointedAtTheRecursiveForm()
    {
        var dir = _ws.CreateDirectory("nested");
        HalfCovered("nested/run/coverage.xml");

        var (code, _, stderr) = await Run("check", dir, "--min-line", "40", "--pattern", "coverage.xml");

        await Assert.That(code).IsEqualTo(3);
        await Assert.That(Lines(stderr)[1]).IsEqualTo(
            $"warning: no file in '{dir}' matches 'coverage.xml'; .xml files there: {Path.Combine("run", "coverage.xml")}; " +
            "try --pattern \"**/coverage.xml\"");
    }

    [Test]
    [Arguments("crap")]
    [Arguments("snapshot")]
    public async Task EveryReportReadingCommand_WarnsAboutAnEmptyDirectory(string command)
    {
        var empty = _ws.CreateDirectory("empty");

        var (_, _, stderr) = await Run(command, empty);

        await Assert.That(stderr).Contains($"warning: no file in '{empty}' matches '**/*cobertura*.xml'{Environment.NewLine}");
    }

    [Test]
    public async Task Diff_EmptyBeforeDirectory_IsNamed_TheAfterFileIsNot()
    {
        var empty = _ws.CreateDirectory("before");
        var after = HalfCovered();

        var (code, stdout, stderr) = await Run("diff", empty, after);

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stdout).Contains("src/A.cs");
        await Assert.That(stderr).IsEqualTo($"warning: no file in '{empty}' matches '**/*cobertura*.xml'{Environment.NewLine}");
    }

    [Test]
    public async Task FileArgument_NeverWarns()
    {
        var (_, _, stderr) = await Run("report", HalfCovered());

        await Assert.That(stderr).IsEmpty();
    }

    // ── A diff lists what moved ──

    [Test]
    [Arguments("table", "Unchanged: 2 files")]
    [Arguments("md", "**Unchanged:** 2 files")]
    public async Task Diff_ListsOnlyTheFilesThatMoved_AndCountsTheRest(string format, string count)
    {
        var before = _ws.Write("before.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/Moved.cs", c => c.Line(1, hits: 1).Line(2, hits: 0))
            .AddClass("src/Still.cs", c => c.Line(1, hits: 1))
            .AddClass("src/AlsoStill.cs", c => c.Line(1, hits: 0))
            .AddClass("src/Gone.cs", c => c.Line(1, hits: 1)));
        var after = _ws.Write("after.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/Moved.cs", c => c.Line(1, hits: 1).Line(2, hits: 1))
            .AddClass("src/Still.cs", c => c.Line(1, hits: 1))
            .AddClass("src/AlsoStill.cs", c => c.Line(1, hits: 0))
            .AddClass("src/New.cs", c => c.Line(1, hits: 1)));

        var (code, stdout, stderr) = await Run("diff", before, after, "--format", format);

        await Assert.That(code).IsEqualTo(0).Because(stderr);
        await Assert.That(stdout).Contains("src/Moved.cs");
        await Assert.That(stdout).Contains("src/Gone.cs");
        await Assert.That(stdout).Contains("src/New.cs");
        await Assert.That(stdout).DoesNotContain("Still.cs");
        await Assert.That(stdout).Contains(count + Environment.NewLine);
    }
}
