using DotCov.Tests.Infrastructure;
using DotCov.Tool;

namespace DotCov.Tests;
/// <summary>
/// Pins the CLI contract of <see cref = "DotCovCli"/>: exit codes, arg parsing, and error paths.
/// These ARE the CI semantics of the tool — a regression in any exit code silently changes
/// what every consumer's pipeline enforces.
/// </summary>
[TestClass]
public sealed class CliTests : IDisposable
{
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-cli-tests-");
    public void Dispose() => _ws.Dispose();
    private static async Task<(int Code, string StdOut, string StdErr)> Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await DotCovCli.RunAsync(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    /// <summary>50% line coverage, no branch data.</summary>
    private string HalfCovered(string relative = "coverage.cobertura.xml") => _ws.Write(relative, Cobertura.NewDoc().AddClass("src/A.cs", c => c.Line(1, hits: 1).Line(2, hits: 0)).ToBytes());
    /// <summary>100% line coverage, 50% branch coverage (1/2).</summary>
    private string BranchHalf(string relative = "branch.cobertura.xml") => _ws.Write(relative, Cobertura.NewDoc().AddClass("src/B.cs", c => c.Line(1, hits: 1).Branch(2, "50% (1/2)")).ToBytes());
    // ── Exit-code matrix ──
    [TestMethod]
    public async Task Check_Pass_Exits0_WithTheVerdictOnStderr()
    {
        // check has no data to print: stdout stays empty, and the verdict leads stderr as on
        // every other outcome.
        var(code, stdout, stderr) = await Run("check", HalfCovered(), "--min-line", "40");
        Assert.AreEqual(0, code);
        Assert.IsEmpty(stdout);
        Assert.AreEqual($"PASS: line 50.0% (min 40%), branch n/a (min 0%) - thresholds met{Environment.NewLine}", stderr);
    }

    [TestMethod]
    public async Task Check_MtpDirectoryWithoutPattern_FindsTimestampedReport()
    {
        HalfCovered("run/dotcov.coverage.cobertura.170926234734218.xml");
        var(code, _, stderr) = await Run("check", _ws.Root, "--min-line", "40");
        Assert.AreEqual(0, code, message: stderr);
        Assert.StartsWith("PASS: line 50.0%", stderr);
    }

    [TestMethod]
    public async Task Check_Fail_Exits1_AndListsOffendingFiles()
    {
        var(code, _, stderr) = await Run("check", HalfCovered(), "--min-line", "90");
        Assert.AreEqual(1, code);
        Assert.Contains("FAIL", stderr);
        Assert.Contains("  src/A.cs  50.0% lines" + Environment.NewLine + "    uncovered 2", stderr);
    }

    [TestMethod]
    public async Task Check_BranchBelowThreshold_Exits1()
    {
        var(code, _, stderr) = await Run("check", BranchHalf(), "--min-line", "50", "--min-branch", "90");
        Assert.AreEqual(1, code);
        Assert.Contains("FAIL", stderr);
        Assert.Contains("branch", stderr);
    }

    [TestMethod]
    public async Task Check_ZeroThresholds_Disabled_Exits4()
    {
        var(code, _, stderr) = await Run("check", HalfCovered(), "--min-line", "0");
        Assert.AreEqual(4, code);
        Assert.Contains("DISABLED", stderr);
    }

    [TestMethod]
    public async Task Check_EmptyDirectory_NoData_Exits3()
    {
        var empty = _ws.CreateDirectory("empty");
        var(code, _, stderr) = await Run("check", empty, "--min-line", "80");
        Assert.AreEqual(3, code);
        Assert.Contains("NODATA", stderr);
    }

    // ── Unknown command / help ──
    [TestMethod]
    public async Task UnknownCommand_SuggestsTheClosestCommand_OnStderr_Exits2()
    {
        // The mistake and its fix, on stderr only: help on stdout would read as the command's data.
        var(code, stdout, stderr) = await Run("chek", "whatever.xml", "--min-line", "80");
        Assert.AreEqual(2, code);
        Assert.IsEmpty(stdout);
        Assert.AreEqual("error: Unknown command 'chek'. Did you mean 'check'?" + Environment.NewLine + "commands: report, check, crap, diff, snapshot, test, version, help" + Environment.NewLine, stderr);
    }

    [TestMethod]
    [DataRow("CHECK", "check")]
    [DataRow("reprot", "report")]
    [DataRow("verison", "version")]
    public async Task UnknownCommand_WithinTwoEdits_NamesTheCommand(string typed, string meant)
    {
        var(code, stdout, stderr) = await Run(typed);
        Assert.AreEqual(2, code);
        Assert.IsEmpty(stdout);
        Assert.StartsWith($"error: Unknown command '{typed}'. Did you mean '{meant}'?", stderr);
    }

    [TestMethod]
    public async Task UnknownCommand_FarFromEveryCommand_SuggestsNone()
    {
        var(code, _, stderr) = await Run("coverage");
        Assert.AreEqual(2, code);
        Assert.StartsWith($"error: Unknown command 'coverage'.{Environment.NewLine}commands: ", stderr);
    }

    [TestMethod]
    [DataRow("help")]
    [DataRow("--help")]
    [DataRow("-h")]
    public async Task ExplicitHelp_Exits0(string arg)
    {
        var(code, stdout, _) = await Run(arg);
        Assert.AreEqual(0, code);
        Assert.Contains("Commands:", stdout);
    }

    [TestMethod]
    public async Task NoArgs_PrintsHelp_Exits0()
    {
        var(code, stdout, _) = await Run();
        Assert.AreEqual(0, code);
        Assert.Contains("Commands:", stdout);
    }

    [TestMethod]
    [DataRow("version")]
    [DataRow("--version")]
    public async Task Version_Exits0(string command)
    {
        var(code, stdout, _) = await Run(command);
        Assert.AreEqual(0, code);
        Assert.StartsWith("dotcov ", stdout);
    }

    // ── Usage errors ──
    [TestMethod]
    [DataRow("report")]
    [DataRow("check")]
    [DataRow("diff")]
    [DataRow("snapshot")]
    public async Task MissingPathArgument_PrintsUsage_Exits2(string command)
    {
        var(code, _, stderr) = await Run(command);
        Assert.AreEqual(2, code);
        Assert.StartsWith($"error: missing path. Usage: dotcov {command}", stderr);
    }

    // ── Arguments a command does not use: an error, never a silently applied default ──
    /// <summary>90% line coverage: passes the default --min-line 80, fails an intended 99.</summary>
    private string NinetyPercent() => _ws.Write("ninety.cobertura.xml", Cobertura.NewDoc().AddClass("src/N.cs", c =>
    {
        for (var line = 1; line <= 9; line++)
            c.Line(line, hits: 1);
        c.Line(10, hits: 0);
    }).ToBytes());
    [TestMethod]
    [DataRow("--min-lin 99")]
    [DataRow("--min-line=99")]
    [DataRow("--max-crap 99")]
    public async Task Check_OptionItDoesNotUse_IsAnError_NotTheDefaultGate(string option)
    {
        var(code, stdout, stderr) = await Run(["check", NinetyPercent(), ..option.Split(' ')]);
        Assert.AreEqual(2, code);
        Assert.DoesNotContain("PASS", stdout + stderr);
        Assert.StartsWith($"error: Unknown option '{option.Split(' ')[0]}' for 'check'.", stderr);
    }

    [TestMethod]
    public async Task Check_SecondPath_IsAnError_NotSilentlyDropped()
    {
        // A shell glob expands to several files, and gating only the first would pass this pair.
        var covered = _ws.Write("glob/a.cobertura.xml", Cobertura.NewDoc().AddClass("src/A.cs", c => c.Line(1, hits: 1)).ToBytes());
        var uncovered = _ws.Write("glob/b.cobertura.xml", Cobertura.NewDoc().AddClass("src/B.cs", c => c.Line(1, hits: 0)).ToBytes());
        var(code, stdout, stderr) = await Run("check", covered, uncovered, "--min-line", "80");
        Assert.AreEqual(2, code);
        Assert.DoesNotContain("PASS", stdout + stderr);
        Assert.StartsWith($"error: Unexpected argument '{uncovered}' for 'check'.", stderr);
    }

    [TestMethod]
    public async Task Keep_WithoutAnExclusion_IsAnError()
    {
        // --keep only exempts files from an exclusion, so alone it would change nothing.
        var(code, _, stderr) = await Run("report", HalfCovered(), "--keep", "Program.cs");
        Assert.AreEqual(2, code);
        Assert.StartsWith("error: --keep only applies together with --exclude-generated, --exclude-test-code or --exclude-external.", stderr);
    }

    [TestMethod]
    [DataRow("report")]
    [DataRow("check")]
    [DataRow("crap")]
    [DataRow("diff")]
    [DataRow("snapshot")]
    public async Task Separator_IsAnError_ForEveryCommandButTest(string command)
    {
        // Only test forwards what follows '--'; anywhere else it would be dropped unread.
        var(code, stdout, stderr) = await Run(command, HalfCovered(), "--", "-c", "Release");
        Assert.AreEqual(2, code);
        Assert.IsEmpty(stdout);
        Assert.StartsWith($"error: Unknown option '--' for '{command}'.", stderr);
    }

    [TestMethod]
    public async Task Test_DotnetTestOptionBeforeTheSeparator_IsAnError_NamingTheSeparator()
    {
        var(code, stdout, stderr) = await Run("test", "--filter", "Slow");
        Assert.AreEqual(2, code);
        Assert.IsEmpty(stdout);
        Assert.StartsWith("error: Unknown option '--filter' for 'test'. Pass dotnet test arguments after '--'.", stderr);
    }

    [TestMethod]
    public async Task Test_SecondPositional_IsAnError_NamingTheSeparator()
    {
        var(code, stdout, stderr) = await Run("test", "App.Tests.csproj", "Release");
        Assert.AreEqual(2, code);
        Assert.IsEmpty(stdout);
        Assert.StartsWith("error: Unexpected argument 'Release' for 'test'. Pass dotnet test arguments after '--'.", stderr);
    }

    [TestMethod]
    public async Task Test_DotnetTestShortOption_IsAnUnknownOption_NamingTheSeparator()
    {
        // A single-dash option after the project is an option, not a second project.
        var(code, stdout, stderr) = await Run("test", "App.Tests.csproj", "-c", "Release");
        Assert.AreEqual(2, code);
        Assert.IsEmpty(stdout);
        Assert.StartsWith($"error: Unknown option '-c' for 'test'. Pass dotnet test arguments after '--'.{Environment.NewLine}valid options for 'test': ", stderr);
    }

    [TestMethod]
    [DataRow("--min-line", "abc")]
    [DataRow("--min-branch", "NaN")]
    public async Task Test_InvalidThreshold_FailsBeforeAnyTestRuns(string flag, string value)
    {
        var(code, stdout, stderr) = await Run("test", flag, value);
        Assert.AreEqual(2, code);
        // No echoed "> dotnet test" line: nothing was started.
        Assert.IsEmpty(stdout);
        Assert.StartsWith($"error: Invalid {flag} value: '{value}'", stderr);
    }

    // ── Invalid numeric flags ──
    [TestMethod]
    [DataRow("--threshold", "abc")]
    [DataRow("--threshold", "NaN")]
    public async Task Report_InvalidThreshold_Exits2(string flag, string value)
    {
        var(code, _, stderr) = await Run("report", HalfCovered(), flag, value);
        Assert.AreEqual(2, code);
        Assert.Contains($"Invalid --threshold value: '{value}'", stderr);
    }

    [TestMethod]
    [CombinatorialData]
    public async Task Check_InvalidThreshold_Exits2_AndNamesValue([CombinatorialValues("--min-line", "--min-branch")] string flag, [CombinatorialValues("eighty", "NaN", "5%")] string value)
    {
        var(code, _, stderr) = await Run("check", HalfCovered(), flag, value);
        Assert.AreEqual(2, code);
        Assert.StartsWith($"error: Invalid {flag} value: '{value}'", stderr);
    }

    // ── --format validation ──
    [TestMethod]
    public async Task Report_InvalidFormat_Exits2()
    {
        var(code, _, stderr) = await Run("report", HalfCovered(), "--format", "yaml");
        Assert.AreEqual(2, code);
        Assert.Contains("Invalid --format value: 'yaml'", stderr);
    }

    [TestMethod]
    public async Task Diff_InvalidFormat_Exits2()
    {
        var path = HalfCovered();
        var(code, _, stderr) = await Run("diff", path, path, "--format", "jsn");
        Assert.AreEqual(2, code);
        Assert.Contains("Invalid --format value: 'jsn'", stderr);
    }

    [TestMethod]
    [DataRow("table")]
    [DataRow("json")]
    [DataRow("markdown")]
    [DataRow("md")]
    public async Task Report_KnownFormats_Exit0(string format)
    {
        var(code, stdout, _) = await Run("report", HalfCovered(), "--format", format);
        Assert.AreEqual(0, code);
        Assert.IsNotEmpty(stdout);
    }

    [TestMethod]
    public async Task Report_DefaultFormat_IsTable()
    {
        var(code, stdout, _) = await Run("report", HalfCovered());
        Assert.AreEqual(0, code);
        Assert.Contains("src/A.cs", stdout);
    }

    // ── Parse/IO failures: friendly one-liners, never stack traces ──
    private static void AssertFriendlyError(string stderr)
    {
        Assert.StartsWith("error:", stderr);
        Assert.DoesNotContain("Unhandled exception", stderr);
        Assert.DoesNotContain("   at ", stderr);
    }

    [TestMethod]
    public async Task Report_MalformedXml_FriendlyError_Exits5()
    {
        var path = _ws.Write("truncated.xml", "<coverage><packages><package");
        var(code, _, stderr) = await Run("report", path);
        Assert.AreEqual(5, code);
        AssertFriendlyError(stderr);
        Assert.Contains(path, stderr);
    }

    [TestMethod]
    public async Task Report_EmptyFile_FriendlyError_Exits5()
    {
        var path = _ws.Write("empty.xml", "");
        var(code, _, stderr) = await Run("report", path);
        Assert.AreEqual(5, code);
        AssertFriendlyError(stderr);
    }

    [TestMethod]
    public async Task Report_DoctypeHeader_ParsesSuccessfully_Exits0()
    {
        // Reference Cobertura always emits a DOCTYPE; the parser ignores the DTD without resolving it.
        var path = _ws.Write("dtd.xml", """<?xml version="1.0"?><!DOCTYPE coverage [<!ENTITY x "y">]><coverage></coverage>""");
        var(code, _, _) = await Run("report", path);
        Assert.AreEqual(0, code);
    }

    [TestMethod]
    public async Task Report_NonexistentPath_FriendlyError_Exits5()
    {
        var(code, _, stderr) = await Run("report", "/no/such/path.xml");
        Assert.AreEqual(5, code);
        AssertFriendlyError(stderr);
        Assert.Contains("/no/such/path.xml", stderr);
    }

    [TestMethod]
    public async Task Diff_NonexistentSecondPath_FriendlyError_Exits5()
    {
        var(code, _, stderr) = await Run("diff", HalfCovered(), "/no/such/after.xml");
        Assert.AreEqual(5, code);
        AssertFriendlyError(stderr);
        Assert.Contains("/no/such/after.xml", stderr);
    }

    [TestMethod]
    public async Task Snapshot_MalformedXml_FriendlyError_Exits5()
    {
        var path = _ws.Write("bad.xml", "<coverage>");
        var(code, _, stderr) = await Run("snapshot", path);
        Assert.AreEqual(5, code);
        AssertFriendlyError(stderr);
    }

    [TestMethod]
    public async Task DirectoryScan_MalformedFile_ErrorNamesOffendingFile()
    {
        HalfCovered("scan/a/coverage.cobertura.xml");
        var bad = _ws.Write("scan/b/coverage.cobertura.xml", "<coverage><packa");
        var(code, _, stderr) = await Run("report", _ws.PathOf("scan"));
        Assert.AreEqual(5, code);
        AssertFriendlyError(stderr);
        Assert.Contains(bad, stderr);
    }

    [TestMethod]
    public async Task DirectoryScan_AggregatesAllReports()
    {
        HalfCovered("agg/a/coverage.cobertura.xml");
        BranchHalf("agg/b/coverage.cobertura.xml");
        var(code, stdout, _) = await Run("report", _ws.PathOf("agg"));
        Assert.AreEqual(0, code);
        Assert.Contains("src/A.cs", stdout);
        Assert.Contains("src/B.cs", stdout);
    }

    [TestMethod]
    public async Task Check_WarningsAndMergedReports_FollowTheVerdictOnStderr()
    {
        // Warnings and the merged inputs are printed so a degraded input or a report left over
        // from an earlier run cannot decide the gate unseen. The verdict stays the first stderr token.
        var degraded = _ws.Write("diag/a/coverage.cobertura.xml", Cobertura.NewDoc().AddClass("src/A.cs", c => c.MalformedLine("1", "lots").Line(2, hits: 1)).ToBytes());
        var other = _ws.Write("diag/b/coverage.cobertura.xml", Cobertura.NewDoc().AddClass("src/B.cs", c => c.Line(1, hits: 0)).ToBytes());
        var dir = _ws.PathOf("diag");
        var(code, _, stderr) = await Run("check", dir, "--min-line", "80");
        Assert.AreEqual(1, code);
        Assert.StartsWith("FAIL:", stderr);
        Assert.Contains("warning: src/A.cs:1: hits='lots' could not be parsed", stderr);
        Assert.Contains($"merged 2 reports from '{dir}':", stderr);
        Assert.Contains($"  {degraded}", stderr);
        Assert.Contains($"  {other}", stderr);
    }

    [TestMethod]
    public async Task Report_Table_WritesWarningDetailsToStderr()
    {
        // The table shows only a warning count, so the details go to stderr.
        var path = _ws.Write("table-warn.cobertura.xml", Cobertura.NewDoc().AddClass("src/A.cs", c => c.MalformedLine("1", "lots").Line(2, hits: 1)).ToBytes());
        var(code, stdout, stderr) = await Run("report", path);
        Assert.AreEqual(0, code);
        Assert.Contains("Warnings: 1", stdout);
        Assert.Contains("warning: src/A.cs:1: hits='lots' could not be parsed", stderr);
    }

    [TestMethod]
    public async Task Diff_WarningsOfBothReports_GoToStderr()
    {
        // Both reports' warnings are printed so a degraded input cannot change the diff unseen.
        var before = _ws.Write("diff-warn/before.xml", Cobertura.NewDoc().AddClass("src/A.cs", c => c.MalformedLine("1", "lots").Line(2, hits: 1)).ToBytes());
        var after = _ws.Write("diff-warn/after.xml", Cobertura.NewDoc().AddClass("src/A.cs", c => c.Line(1, hits: 1).MalformedLine("2", "many")).ToBytes());
        var(code, _, stderr) = await Run("diff", before, after);
        Assert.AreEqual(0, code);
        Assert.Contains("warning: src/A.cs:1: hits='lots' could not be parsed", stderr);
        Assert.Contains("warning: src/A.cs:2: hits='many' could not be parsed", stderr);
    }

    // ── Upload failures ──
    [TestMethod]
    public async Task Report_UploadMalformedUrl_FriendlyError_Exits2()
    {
        var(code, _, stderr) = await Run("report", HalfCovered(), "--upload", "notaurl");
        Assert.AreEqual(2, code);
        Assert.StartsWith("error: Invalid --upload value: 'notaurl'", stderr);
        Assert.DoesNotContain("Unhandled exception", stderr);
    }

    [TestMethod]
    public async Task Snapshot_UploadConnectionRefused_Exits5_AfterWritingJson()
    {
        var(code, stdout, stderr) = await Run("snapshot", HalfCovered(), "--commit", "abc123", "--upload", "http://127.0.0.1:1/x");
        Assert.AreEqual(5, code);
        Assert.Contains("abc123", stdout);
        // The missing --branch/--project warning follows; the upload error leads.
        Assert.StartsWith("error: Upload failed: http://127.0.0.1:1/x", stderr);
        Assert.DoesNotContain("Unhandled exception", stderr);
    }

    [TestMethod]
    public async Task Check_PassingGate_UploadFailure_Exits5()
    {
        // Two reports merge, so stderr also lists them. The verdict leads; the upload error that
        // turned the pass into exit 5 follows it, ahead of the diagnostics.
        HalfCovered("up/a/coverage.cobertura.xml");
        HalfCovered("up/b/coverage.cobertura.xml");
        var(code, _, stderr) = await Run("check", _ws.PathOf("up"), "--min-line", "40", "--upload", "http://127.0.0.1:1/x");
        Assert.AreEqual(5, code);
        var lines = stderr.Split(Environment.NewLine);
        Assert.StartsWith("PASS: line 50.0% (min 40%)", lines[0]);
        Assert.StartsWith("error: Upload failed: http://127.0.0.1:1/x", lines[1]);
        Assert.StartsWith("merged 2 reports from ", lines[2]);
    }

    [TestMethod]
    public async Task Check_FailingGate_StillAttemptsUpload_Exits1()
    {
        // Failing runs are the ones a dashboard most needs; the upload is attempted and the
        // gate's exit 1 wins regardless of the upload outcome.
        var(code, _, stderr) = await Run("check", HalfCovered(), "--min-line", "99", "--upload", "http://127.0.0.1:1/x");
        Assert.AreEqual(1, code);
        Assert.Contains("FAIL", stderr);
        Assert.Contains("Upload failed", stderr);
    }

    // ── Positional arguments ──
    [TestMethod]
    public async Task Diff_ThirdPositional_IsRejected()
    {
        // diff takes exactly before and after; a further path is reported, never dropped.
        var path = HalfCovered();
        var(code, stdout, stderr) = await Run("diff", path, path, "extra");
        Assert.AreEqual(2, code);
        Assert.IsEmpty(stdout);
        Assert.StartsWith("error: Unexpected argument 'extra' for 'diff'.", stderr);
    }

    [TestMethod]
    public async Task Report_BooleanFlagBeforePath_Exits0()
    {
        var(code, stdout, _) = await Run("report", "--exclude-generated", HalfCovered());
        Assert.AreEqual(0, code);
        Assert.Contains("src/A.cs", stdout);
    }

    // ── Directory-scan error attribution ──
    [TestMethod]
    public async Task DirectoryScan_MalformedFile_PrefixesPathExactlyOnce()
    {
        // ReportParseException carries the failing path once; the CLI renders it once
        // ("error: {path}: ..."), never "error: {path}: {path}: ...".
        var bad = _ws.Write("once/coverage.cobertura.xml", "<coverage><packa");
        var(code, _, stderr) = await Run("report", _ws.PathOf("once"));
        Assert.AreEqual(5, code);
        Assert.Contains($"error: {bad}:", stderr);
        Assert.DoesNotContain($"{bad}: {bad}:", stderr);
    }

    // ── --pattern ──
    [TestMethod]
    public async Task Report_Pattern_DiscoversNonDefaultFilenames()
    {
        HalfCovered("gcovr/sub/coverage.xml");
        var dir = _ws.PathOf("gcovr");
        // The default Cobertura-name pattern excludes gcovr's generic coverage.xml.
        var(defaultCode, defaultOut, _) = await Run("report", dir);
        Assert.AreEqual(0, defaultCode);
        Assert.DoesNotContain("src/A.cs", defaultOut);
        var(code, stdout, _) = await Run("report", dir, "--pattern", "**/coverage.xml");
        Assert.AreEqual(0, code);
        Assert.Contains("src/A.cs", stdout);
    }

    [TestMethod]
    public async Task Report_Pattern_TopLevelShape_DoesNotRecurse()
    {
        HalfCovered("toplevel/cobertura.xml");
        BranchHalf("toplevel/nested/cobertura.xml");
        var(code, stdout, _) = await Run("report", _ws.PathOf("toplevel"), "--pattern", "cobertura.xml");
        Assert.AreEqual(0, code);
        Assert.Contains("src/A.cs", stdout);
        Assert.DoesNotContain("src/B.cs", stdout);
    }

    [TestMethod]
    public async Task Check_Pattern_GatesNonDefaultFilenames()
    {
        HalfCovered("chk/coverage.xml");
        var(code, _, stderr) = await Run("check", _ws.PathOf("chk"), "--min-line", "90", "--pattern", "**/coverage.xml");
        Assert.AreEqual(1, code);
        Assert.Contains("FAIL", stderr);
    }

    [TestMethod]
    public async Task Report_InvalidPattern_FriendlyError_Exits2()
    {
        // An invalid --pattern is rejected at the option boundary as a one-line CLI error,
        // never a stack trace.
        var dir = _ws.CreateDirectory("pat");
        var(code, _, stderr) = await Run("report", dir, "--pattern", "sub/coverage.xml");
        Assert.AreEqual(2, code);
        AssertFriendlyError(stderr);
        Assert.Contains("Unsupported pattern", stderr);
    }

    // ── --max-chars ──
    [TestMethod]
    public async Task Report_MaxChars_BelowDocumentSize_FriendlyError_Exits5()
    {
        var path = HalfCovered();
        var(code, _, stderr) = await Run("report", path, "--max-chars", "10");
        Assert.AreEqual(5, code);
        AssertFriendlyError(stderr);
        Assert.Contains(path, stderr);
        Assert.Contains("MaxCharactersInDocument", stderr);
    }

    [TestMethod]
    public async Task Report_MaxChars_ZeroDisablesCap_Exits0()
    {
        var(code, _, _) = await Run("report", HalfCovered(), "--max-chars", "0");
        Assert.AreEqual(0, code);
    }

    [TestMethod]
    [DataRow("abc")]
    [DataRow("-1")]
    [DataRow("1.5")]
    public async Task Report_InvalidMaxChars_Exits2_AndNamesValue(string value)
    {
        var(code, _, stderr) = await Run("report", HalfCovered(), "--max-chars", value);
        Assert.AreEqual(2, code);
        Assert.Contains($"Invalid --max-chars value: '{value}'", stderr);
    }

    [TestMethod]
    public async Task Check_MaxChars_CapOverflow_IsErrorNotFail()
    {
        // A size-cap overflow during check is a could-not-measure error ("error:" first token),
        // never presented as a coverage FAIL: it returns input/IO error 5, not threshold 1.
        var(code, _, stderr) = await Run("check", HalfCovered(), "--min-line", "40", "--max-chars", "10");
        Assert.AreEqual(5, code);
        Assert.StartsWith("error:", stderr);
        Assert.DoesNotContain("FAIL", stderr);
    }

    // ── Offender list scoping and flooring ──
    [TestMethod]
    public async Task Check_LineFailure_LabelsOffenderList()
    {
        var(code, _, stderr) = await Run("check", HalfCovered(), "--min-line", "90");
        Assert.AreEqual(1, code);
        Assert.Contains("files below line threshold:" + Environment.NewLine + "  src/A.cs  50.0% lines", stderr);
    }

    [TestMethod]
    public async Task Check_BranchOnlyFailure_ListsOnlyTheFilesBelowTheBranchThreshold()
    {
        // Line gate passes overall (5/7 ≈ 71.4% ≥ 50) but B.cs sits below min-line; the branch
        // gate fails. B.cs has zero failing branches and must not be blamed for the failure;
        // A.cs, whose branch is half covered, is.
        var path = _ws.Write("mixed.cobertura.xml", Cobertura.NewDoc().AddClass("src/A.cs", c => c.Line(1, hits: 1).Line(2, hits: 1).Line(3, hits: 1).Branch(4, "50% (1/2)")).AddClass("src/B.cs", c => c.Line(1, hits: 1).Line(2, hits: 0).Line(3, hits: 0)).ToBytes());
        var(code, _, stderr) = await Run("check", path, "--min-line", "50", "--min-branch", "90");
        Assert.AreEqual(1, code);
        Assert.Contains("FAIL", stderr);
        Assert.Contains("branch coverage below threshold", stderr);
        Assert.Contains("files below branch threshold:" + Environment.NewLine + "  src/A.cs  100.0% lines  50.0% branches" + Environment.NewLine + "    branches 4 (1/2)", stderr);
        Assert.DoesNotContain("src/B.cs", stderr);
    }

    [TestMethod]
    public async Task Check_NoData_PrintsNoOffenderList()
    {
        var empty = _ws.CreateDirectory("empty-nolist");
        var(code, _, stderr) = await Run("check", empty, "--min-line", "80");
        Assert.AreEqual(3, code);
        Assert.Contains("NODATA", stderr);
        Assert.DoesNotContain("files below line threshold", stderr);
    }

    [TestMethod]
    public async Task Check_OffenderList_FloorsFailingRate()
    {
        // 1999/2500 = 79.96%: must floor to 79.9%, never F1-round up to the missed minimum.
        var path = _ws.Write("floor.cobertura.xml", CoberturaSamples.JustBelowEightyPercent().ToBytes());
        var(code, _, stderr) = await Run("check", path, "--min-line", "80");
        Assert.AreEqual(1, code);
        Assert.Contains("  src/F.cs  79.9% lines", stderr);
        Assert.DoesNotContain("80.0%", stderr);
    }

    // ── Unsupported upload scheme ──
    [TestMethod]
    public async Task Report_UploadUnsupportedScheme_FriendlyError_Exits2()
    {
        // Unsupported upload schemes are usage errors before any IO.
        var(code, _, stderr) = await Run("report", HalfCovered(), "--upload", "ftp://example.invalid/x");
        Assert.AreEqual(2, code);
        Assert.Contains("Invalid --upload value: 'ftp://example.invalid/x'", stderr);
        Assert.DoesNotContain("Unhandled exception", stderr);
    }

    // ── Snapshot identity defaults ──
    [TestMethod]
    public async Task Snapshot_MissingIdentityFlags_WarnsButExits0()
    {
        var(code, stdout, stderr) = await Run("snapshot", HalfCovered());
        Assert.AreEqual(0, code);
        Assert.Contains("unknown", stdout);
        Assert.Contains("warning: --commit, --branch, --project not provided; snapshot stamped 'unknown'", stderr);
    }

    [TestMethod]
    public async Task Snapshot_PartialIdentityFlags_WarnsOnlyMissing()
    {
        var(code, _, stderr) = await Run("snapshot", HalfCovered(), "--commit", "abc123");
        Assert.AreEqual(0, code);
        Assert.Contains("--branch, --project not provided", stderr);
        Assert.DoesNotContain("--commit", stderr);
    }

    [TestMethod]
    public async Task Snapshot_AllIdentityFlags_NoWarning()
    {
        var(code, _, stderr) = await Run("snapshot", HalfCovered(), "--commit", "abc123", "--branch", "main", "--project", "MyApp");
        Assert.AreEqual(0, code);
        Assert.DoesNotContain("warning:", stderr);
    }

    [TestMethod]
    public async Task Snapshot_Directory_HashesTheMergedReports()
    {
        HalfCovered("snap/a/coverage.cobertura.xml");
        BranchHalf("snap/b/coverage.cobertura.xml");
        var(code, stdout, _) = await Run("snapshot", _ws.PathOf("snap"), "--commit", "abc123", "--branch", "main", "--project", "MyApp");
        Assert.AreEqual(0, code);
        var hash = System.Text.Json.JsonDocument.Parse(stdout).RootElement.GetProperty("fileHash").GetString();
        Assert.IsNotNull(hash);
        Assert.AreEqual(64, hash.Length);
    }

    // ── Documented CLI contract ──
    [TestMethod]
    public async Task Help_DocumentsExitCodesAndFlags()
    {
        var(code, stdout, _) = await Run("help");
        Assert.AreEqual(0, code);
        // One table, every code once and in order.
        Assert.HasCount(2, stdout.Split("Exit codes:"));
        var table = stdout[stdout.IndexOf("Exit codes:", StringComparison.Ordinal)..];
        var at = 0;
        foreach (var row in new[]
        {
            "0  success;",
            "1  FAIL:",
            "2  usage error:",
            "3  NODATA:",
            "4  DISABLED:",
            "5  input/IO error:",
            "6  test run failed:"
        }

        )
        {
            var next = table.IndexOf($"{Environment.NewLine}  {row}", at, StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(at, next, message: row);
            at = next + 1;
        }

        Assert.Contains("--pattern", stdout);
        Assert.Contains("--max-chars", stdout);
        Assert.Contains("--badge-dir <dir>", stdout);
        Assert.Contains("--no-color", stdout);
        Assert.Contains("NO_COLOR=1", stdout);
    }
}
