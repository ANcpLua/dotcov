using DotCov.Tests.Infrastructure;
using DotCov.Tool;

namespace DotCov.Tests;

/// <summary>
/// Pins the CLI contract of <see cref="DotCovCli"/>: exit codes, arg parsing, and error paths.
/// These ARE the CI semantics of the tool — a regression in any exit code silently changes
/// what every consumer's pipeline enforces.
/// </summary>
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
    private string HalfCovered(string relative = "coverage.cobertura.xml") =>
        _ws.Write(relative, Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c.Line(1, hits: 1).Line(2, hits: 0))
            .ToBytes());

    /// <summary>100% line coverage, 50% branch coverage (1/2).</summary>
    private string BranchHalf(string relative = "branch.cobertura.xml") =>
        _ws.Write(relative, Cobertura.NewDoc()
            .AddClass("src/B.cs", c => c.Line(1, hits: 1).Branch(2, "50% (1/2)"))
            .ToBytes());

    // ── Exit-code matrix ──

    [Test]
    public async Task Check_Pass_Exits0_WithTheVerdictOnStderr()
    {
        // check has no data to print: stdout stays empty, and the verdict leads stderr as on
        // every other outcome.
        var (code, stdout, stderr) = await Run("check", HalfCovered(), "--min-line", "40");

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stdout).IsEmpty();
        await Assert.That(stderr).IsEqualTo($"PASS: line 50.0% (min 40%), branch n/a (min 0%) - thresholds met{Environment.NewLine}");
    }

    [Test]
    public async Task Check_MtpDirectoryWithoutPattern_FindsTimestampedReport()
    {
        HalfCovered("run/dotcov.coverage.cobertura.170926234734218.xml");

        var (code, _, stderr) = await Run("check", _ws.Root, "--min-line", "40");

        await Assert.That(code).IsEqualTo(0).Because(stderr);
        await Assert.That(stderr).StartsWith("PASS: line 50.0%");
    }

    [Test]
    public async Task Check_Fail_Exits1_AndListsOffendingFiles()
    {
        var (code, _, stderr) = await Run("check", HalfCovered(), "--min-line", "90");

        await Assert.That(code).IsEqualTo(1);
        await Assert.That(stderr).Contains("FAIL");
        await Assert.That(stderr).Contains("  src/A.cs  50.0% lines" + Environment.NewLine + "    uncovered 2");
    }

    [Test]
    public async Task Check_BranchBelowThreshold_Exits1()
    {
        var (code, _, stderr) = await Run("check", BranchHalf(), "--min-line", "50", "--min-branch", "90");

        await Assert.That(code).IsEqualTo(1);
        await Assert.That(stderr).Contains("FAIL");
        await Assert.That(stderr).Contains("branch");
    }

    [Test]
    public async Task Check_ZeroThresholds_Disabled_Exits4()
    {
        var (code, _, stderr) = await Run("check", HalfCovered(), "--min-line", "0");

        await Assert.That(code).IsEqualTo(4);
        await Assert.That(stderr).Contains("DISABLED");
    }

    [Test]
    public async Task Check_EmptyDirectory_NoData_Exits3()
    {
        var empty = _ws.CreateDirectory("empty");

        var (code, _, stderr) = await Run("check", empty, "--min-line", "80");

        await Assert.That(code).IsEqualTo(3);
        await Assert.That(stderr).Contains("NODATA");
    }

    // ── Unknown command / help ──

    [Test]
    public async Task UnknownCommand_SuggestsTheClosestCommand_OnStderr_Exits2()
    {
        // The mistake and its fix, on stderr only: help on stdout would read as the command's data.
        var (code, stdout, stderr) = await Run("chek", "whatever.xml", "--min-line", "80");

        await Assert.That(code).IsEqualTo(2);
        await Assert.That(stdout).IsEmpty();
        await Assert.That(stderr).IsEqualTo(
            "error: Unknown command 'chek'. Did you mean 'check'?" + Environment.NewLine +
            "commands: report, check, crap, diff, snapshot, test, version, help" + Environment.NewLine);
    }

    [Test]
    [Arguments("CHECK", "check")]
    [Arguments("reprot", "report")]
    [Arguments("verison", "version")]
    public async Task UnknownCommand_WithinTwoEdits_NamesTheCommand(string typed, string meant)
    {
        var (code, stdout, stderr) = await Run(typed);

        await Assert.That(code).IsEqualTo(2);
        await Assert.That(stdout).IsEmpty();
        await Assert.That(stderr).StartsWith($"error: Unknown command '{typed}'. Did you mean '{meant}'?");
    }

    [Test]
    public async Task UnknownCommand_FarFromEveryCommand_SuggestsNone()
    {
        var (code, _, stderr) = await Run("coverage");

        await Assert.That(code).IsEqualTo(2);
        await Assert.That(stderr).StartsWith($"error: Unknown command 'coverage'.{Environment.NewLine}commands: ");
    }

    [Test]
    [Arguments("help")]
    [Arguments("--help")]
    [Arguments("-h")]
    public async Task ExplicitHelp_Exits0(string arg)
    {
        var (code, stdout, _) = await Run(arg);

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stdout).Contains("Commands:");
    }

    [Test]
    public async Task NoArgs_PrintsHelp_Exits0()
    {
        var (code, stdout, _) = await Run();

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stdout).Contains("Commands:");
    }

    [Test]
    [Arguments("version")]
    [Arguments("--version")]
    public async Task Version_Exits0(string command)
    {
        var (code, stdout, _) = await Run(command);

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stdout).StartsWith("dotcov ");
    }

    // ── Usage errors ──

    [Test]
    [Arguments("report")]
    [Arguments("check")]
    [Arguments("diff")]
    [Arguments("snapshot")]
    public async Task MissingPathArgument_PrintsUsage_Exits2(string command)
    {
        var (code, _, stderr) = await Run(command);

        await Assert.That(code).IsEqualTo(2);
        await Assert.That(stderr).StartsWith($"error: missing path. Usage: dotcov {command}");
    }

    // ── Arguments a command does not use: an error, never a silently applied default ──

    /// <summary>90% line coverage: passes the default --min-line 80, fails an intended 99.</summary>
    private string NinetyPercent() =>
        _ws.Write("ninety.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/N.cs", c =>
            {
                for (var line = 1; line <= 9; line++) c.Line(line, hits: 1);
                c.Line(10, hits: 0);
            })
            .ToBytes());

    [Test]
    [Arguments("--min-lin 99")]
    [Arguments("--min-line=99")]
    [Arguments("--max-crap 99")]
    public async Task Check_OptionItDoesNotUse_IsAnError_NotTheDefaultGate(string option)
    {
        var (code, stdout, stderr) = await Run(["check", NinetyPercent(), .. option.Split(' ')]);

        await Assert.That(code).IsEqualTo(2);
        await Assert.That(stdout + stderr).DoesNotContain("PASS");
        await Assert.That(stderr).StartsWith($"error: Unknown option '{option.Split(' ')[0]}' for 'check'.");
    }

    [Test]
    public async Task Check_SecondPath_IsAnError_NotSilentlyDropped()
    {
        // A shell glob expands to several files, and gating only the first would pass this pair.
        var covered = _ws.Write("glob/a.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c.Line(1, hits: 1))
            .ToBytes());
        var uncovered = _ws.Write("glob/b.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/B.cs", c => c.Line(1, hits: 0))
            .ToBytes());

        var (code, stdout, stderr) = await Run("check", covered, uncovered, "--min-line", "80");

        await Assert.That(code).IsEqualTo(2);
        await Assert.That(stdout + stderr).DoesNotContain("PASS");
        await Assert.That(stderr).StartsWith($"error: Unexpected argument '{uncovered}' for 'check'.");
    }

    [Test]
    public async Task Keep_WithoutAnExclusion_IsAnError()
    {
        // --keep only exempts files from an exclusion, so alone it would change nothing.
        var (code, _, stderr) = await Run("report", HalfCovered(), "--keep", "Program.cs");

        await Assert.That(code).IsEqualTo(2);
        await Assert.That(stderr).StartsWith("error: --keep only applies together with --exclude-generated or --exclude-test-code.");
    }

    [Test]
    [Arguments("report")]
    [Arguments("check")]
    [Arguments("crap")]
    [Arguments("diff")]
    [Arguments("snapshot")]
    public async Task Separator_IsAnError_ForEveryCommandButTest(string command)
    {
        // Only test forwards what follows '--'; anywhere else it would be dropped unread.
        var (code, stdout, stderr) = await Run(command, HalfCovered(), "--", "-c", "Release");

        await Assert.That(code).IsEqualTo(2);
        await Assert.That(stdout).IsEmpty();
        await Assert.That(stderr).StartsWith($"error: Unknown option '--' for '{command}'.");
    }

    [Test]
    public async Task Test_DotnetTestOptionBeforeTheSeparator_IsAnError_NamingTheSeparator()
    {
        var (code, stdout, stderr) = await Run("test", "--filter", "Slow");

        await Assert.That(code).IsEqualTo(2);
        await Assert.That(stdout).IsEmpty();
        await Assert.That(stderr).StartsWith("error: Unknown option '--filter' for 'test'. Pass dotnet test arguments after '--'.");
    }

    [Test]
    public async Task Test_SecondPositional_IsAnError_NamingTheSeparator()
    {
        var (code, stdout, stderr) = await Run("test", "App.Tests.csproj", "Release");

        await Assert.That(code).IsEqualTo(2);
        await Assert.That(stdout).IsEmpty();
        await Assert.That(stderr).StartsWith("error: Unexpected argument 'Release' for 'test'. Pass dotnet test arguments after '--'.");
    }

    [Test]
    public async Task Test_DotnetTestShortOption_IsAnUnknownOption_NamingTheSeparator()
    {
        // A single-dash option after the project is an option, not a second project.
        var (code, stdout, stderr) = await Run("test", "App.Tests.csproj", "-c", "Release");

        await Assert.That(code).IsEqualTo(2);
        await Assert.That(stdout).IsEmpty();
        await Assert.That(stderr).StartsWith(
            $"error: Unknown option '-c' for 'test'. Pass dotnet test arguments after '--'.{Environment.NewLine}valid options for 'test': ");
    }

    [Test]
    [Arguments("--min-line", "abc")]
    [Arguments("--min-branch", "NaN")]
    public async Task Test_InvalidThreshold_FailsBeforeAnyTestRuns(string flag, string value)
    {
        var (code, stdout, stderr) = await Run("test", flag, value);

        await Assert.That(code).IsEqualTo(2);
        // No echoed "> dotnet test" line: nothing was started.
        await Assert.That(stdout).IsEmpty();
        await Assert.That(stderr).StartsWith($"error: Invalid {flag} value: '{value}'");
    }

    // ── Invalid numeric flags ──

    [Test]
    [Arguments("--threshold", "abc")]
    [Arguments("--threshold", "NaN")]
    public async Task Report_InvalidThreshold_Exits2(string flag, string value)
    {
        var (code, _, stderr) = await Run("report", HalfCovered(), flag, value);

        await Assert.That(code).IsEqualTo(2);
        await Assert.That(stderr).Contains($"Invalid --threshold value: '{value}'");
    }

    [Test]
    [MatrixDataSource]
    public async Task Check_InvalidThreshold_Exits2_AndNamesValue(
        [Matrix("--min-line", "--min-branch")] string flag,
        [Matrix("eighty", "NaN", "5%")] string value)
    {
        var (code, _, stderr) = await Run("check", HalfCovered(), flag, value);

        await Assert.That(code).IsEqualTo(2);
        await Assert.That(stderr).StartsWith($"error: Invalid {flag} value: '{value}'");
    }

    // ── --format validation ──

    [Test]
    public async Task Report_InvalidFormat_Exits2()
    {
        var (code, _, stderr) = await Run("report", HalfCovered(), "--format", "yaml");

        await Assert.That(code).IsEqualTo(2);
        await Assert.That(stderr).Contains("Invalid --format value: 'yaml'");
    }

    [Test]
    public async Task Diff_InvalidFormat_Exits2()
    {
        var path = HalfCovered();

        var (code, _, stderr) = await Run("diff", path, path, "--format", "jsn");

        await Assert.That(code).IsEqualTo(2);
        await Assert.That(stderr).Contains("Invalid --format value: 'jsn'");
    }

    [Test]
    [Arguments("table")]
    [Arguments("json")]
    [Arguments("markdown")]
    [Arguments("md")]
    public async Task Report_KnownFormats_Exit0(string format)
    {
        var (code, stdout, _) = await Run("report", HalfCovered(), "--format", format);

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stdout).IsNotEmpty();
    }

    [Test]
    public async Task Report_DefaultFormat_IsTable()
    {
        var (code, stdout, _) = await Run("report", HalfCovered());

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stdout).Contains("src/A.cs");
    }

    // ── Parse/IO failures: friendly one-liners, never stack traces ──

    private static async Task AssertFriendlyError(string stderr)
    {
        await Assert.That(stderr).StartsWith("error:");
        await Assert.That(stderr).DoesNotContain("Unhandled exception");
        await Assert.That(stderr).DoesNotContain("   at ");
    }

    [Test]
    public async Task Report_MalformedXml_FriendlyError_Exits5()
    {
        var path = _ws.Write("truncated.xml", "<coverage><packages><package");

        var (code, _, stderr) = await Run("report", path);

        await Assert.That(code).IsEqualTo(5);
        await AssertFriendlyError(stderr);
        await Assert.That(stderr).Contains(path);
    }

    [Test]
    public async Task Report_EmptyFile_FriendlyError_Exits5()
    {
        var path = _ws.Write("empty.xml", "");

        var (code, _, stderr) = await Run("report", path);

        await Assert.That(code).IsEqualTo(5);
        await AssertFriendlyError(stderr);
    }

    [Test]
    public async Task Report_DoctypeHeader_ParsesSuccessfully_Exits0()
    {
        // Reference Cobertura always emits a DOCTYPE; the parser ignores the DTD without resolving it.
        var path = _ws.Write("dtd.xml",
            """<?xml version="1.0"?><!DOCTYPE coverage [<!ENTITY x "y">]><coverage></coverage>""");

        var (code, _, _) = await Run("report", path);

        await Assert.That(code).IsEqualTo(0);
    }

    [Test]
    public async Task Report_NonexistentPath_FriendlyError_Exits5()
    {
        var (code, _, stderr) = await Run("report", "/no/such/path.xml");

        await Assert.That(code).IsEqualTo(5);
        await AssertFriendlyError(stderr);
        await Assert.That(stderr).Contains("/no/such/path.xml");
    }

    [Test]
    public async Task Diff_NonexistentSecondPath_FriendlyError_Exits5()
    {
        var (code, _, stderr) = await Run("diff", HalfCovered(), "/no/such/after.xml");

        await Assert.That(code).IsEqualTo(5);
        await AssertFriendlyError(stderr);
        await Assert.That(stderr).Contains("/no/such/after.xml");
    }

    [Test]
    public async Task Snapshot_MalformedXml_FriendlyError_Exits5()
    {
        var path = _ws.Write("bad.xml", "<coverage>");

        var (code, _, stderr) = await Run("snapshot", path);

        await Assert.That(code).IsEqualTo(5);
        await AssertFriendlyError(stderr);
    }

    [Test]
    public async Task DirectoryScan_MalformedFile_ErrorNamesOffendingFile()
    {
        HalfCovered("scan/a/coverage.cobertura.xml");
        var bad = _ws.Write("scan/b/coverage.cobertura.xml", "<coverage><packa");

        var (code, _, stderr) = await Run("report", _ws.PathOf("scan"));

        await Assert.That(code).IsEqualTo(5);
        await AssertFriendlyError(stderr);
        await Assert.That(stderr).Contains(bad);
    }

    [Test]
    public async Task DirectoryScan_AggregatesAllReports()
    {
        HalfCovered("agg/a/coverage.cobertura.xml");
        BranchHalf("agg/b/coverage.cobertura.xml");

        var (code, stdout, _) = await Run("report", _ws.PathOf("agg"));

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stdout).Contains("src/A.cs");
        await Assert.That(stdout).Contains("src/B.cs");
    }

    [Test]
    public async Task Check_WarningsAndMergedReports_FollowTheVerdictOnStderr()
    {
        // Warnings and the merged inputs are printed so a degraded input or a report left over
        // from an earlier run cannot decide the gate unseen. The verdict stays the first stderr token.
        var degraded = _ws.Write("diag/a/coverage.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c.MalformedLine("1", "lots").Line(2, hits: 1))
            .ToBytes());
        var other = _ws.Write("diag/b/coverage.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/B.cs", c => c.Line(1, hits: 0))
            .ToBytes());
        var dir = _ws.PathOf("diag");

        var (code, _, stderr) = await Run("check", dir, "--min-line", "80");

        await Assert.That(code).IsEqualTo(1);
        await Assert.That(stderr).StartsWith("FAIL:");
        await Assert.That(stderr).Contains("warning: src/A.cs:1: hits='lots' could not be parsed");
        await Assert.That(stderr).Contains($"merged 2 reports from '{dir}':");
        await Assert.That(stderr).Contains($"  {degraded}");
        await Assert.That(stderr).Contains($"  {other}");
    }

    [Test]
    public async Task Report_Table_WritesWarningDetailsToStderr()
    {
        // The table shows only a warning count, so the details go to stderr.
        var path = _ws.Write("table-warn.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c.MalformedLine("1", "lots").Line(2, hits: 1))
            .ToBytes());

        var (code, stdout, stderr) = await Run("report", path);

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stdout).Contains("Warnings: 1");
        await Assert.That(stderr).Contains("warning: src/A.cs:1: hits='lots' could not be parsed");
    }

    [Test]
    public async Task Diff_WarningsOfBothReports_GoToStderr()
    {
        // Both reports' warnings are printed so a degraded input cannot change the diff unseen.
        var before = _ws.Write("diff-warn/before.xml", Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c.MalformedLine("1", "lots").Line(2, hits: 1))
            .ToBytes());
        var after = _ws.Write("diff-warn/after.xml", Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c.Line(1, hits: 1).MalformedLine("2", "many"))
            .ToBytes());

        var (code, _, stderr) = await Run("diff", before, after);

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stderr).Contains("warning: src/A.cs:1: hits='lots' could not be parsed");
        await Assert.That(stderr).Contains("warning: src/A.cs:2: hits='many' could not be parsed");
    }

    // ── Upload failures ──

    [Test]
    public async Task Report_UploadMalformedUrl_FriendlyError_Exits2()
    {
        var (code, _, stderr) = await Run("report", HalfCovered(), "--upload", "notaurl");

        await Assert.That(code).IsEqualTo(2);
        await Assert.That(stderr).StartsWith("error: Invalid --upload value: 'notaurl'");
        await Assert.That(stderr).DoesNotContain("Unhandled exception");
    }

    [Test]
    public async Task Snapshot_UploadConnectionRefused_Exits5_AfterWritingJson()
    {
        var (code, stdout, stderr) = await Run(
            "snapshot", HalfCovered(), "--commit", "abc123", "--upload", "http://127.0.0.1:1/x");

        await Assert.That(code).IsEqualTo(5);
        await Assert.That(stdout).Contains("abc123");
        // The missing --branch/--project warning follows; the upload error leads.
        await Assert.That(stderr).StartsWith("error: Upload failed: http://127.0.0.1:1/x");
        await Assert.That(stderr).DoesNotContain("Unhandled exception");
    }

    [Test]
    public async Task Check_PassingGate_UploadFailure_Exits5()
    {
        // Two reports merge, so stderr also lists them. The verdict leads; the upload error that
        // turned the pass into exit 5 follows it, ahead of the diagnostics.
        HalfCovered("up/a/coverage.cobertura.xml");
        HalfCovered("up/b/coverage.cobertura.xml");

        var (code, _, stderr) = await Run(
            "check", _ws.PathOf("up"), "--min-line", "40", "--upload", "http://127.0.0.1:1/x");

        await Assert.That(code).IsEqualTo(5);
        var lines = stderr.Split(Environment.NewLine);
        await Assert.That(lines[0]).StartsWith("PASS: line 50.0% (min 40%)");
        await Assert.That(lines[1]).StartsWith("error: Upload failed: http://127.0.0.1:1/x");
        await Assert.That(lines[2]).StartsWith("merged 2 reports from ");
    }

    [Test]
    public async Task Check_FailingGate_StillAttemptsUpload_Exits1()
    {
        // Failing runs are the ones a dashboard most needs; the upload is attempted and the
        // gate's exit 1 wins regardless of the upload outcome.
        var (code, _, stderr) = await Run(
            "check", HalfCovered(), "--min-line", "99", "--upload", "http://127.0.0.1:1/x");

        await Assert.That(code).IsEqualTo(1);
        await Assert.That(stderr).Contains("FAIL");
        await Assert.That(stderr).Contains("Upload failed");
    }

    // ── Positional arguments ──

    [Test]
    public async Task Diff_ThirdPositional_IsRejected()
    {
        // diff takes exactly before and after; a further path is reported, never dropped.
        var path = HalfCovered();

        var (code, stdout, stderr) = await Run("diff", path, path, "extra");

        await Assert.That(code).IsEqualTo(2);
        await Assert.That(stdout).IsEmpty();
        await Assert.That(stderr).StartsWith("error: Unexpected argument 'extra' for 'diff'.");
    }

    [Test]
    public async Task Report_BooleanFlagBeforePath_Exits0()
    {
        var (code, stdout, _) = await Run("report", "--exclude-generated", HalfCovered());

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stdout).Contains("src/A.cs");
    }

    // ── Directory-scan error attribution ──

    [Test]
    public async Task DirectoryScan_MalformedFile_PrefixesPathExactlyOnce()
    {
        // ReportParseException carries the failing path once; the CLI renders it once
        // ("error: {path}: ..."), never "error: {path}: {path}: ...".
        var bad = _ws.Write("once/coverage.cobertura.xml", "<coverage><packa");

        var (code, _, stderr) = await Run("report", _ws.PathOf("once"));

        await Assert.That(code).IsEqualTo(5);
        await Assert.That(stderr).Contains($"error: {bad}:");
        await Assert.That(stderr).DoesNotContain($"{bad}: {bad}:");
    }

    // ── --pattern ──

    [Test]
    public async Task Report_Pattern_DiscoversNonDefaultFilenames()
    {
        HalfCovered("gcovr/sub/coverage.xml");
        var dir = _ws.PathOf("gcovr");

        // The default Cobertura-name pattern excludes gcovr's generic coverage.xml.
        var (defaultCode, defaultOut, _) = await Run("report", dir);
        await Assert.That(defaultCode).IsEqualTo(0);
        await Assert.That(defaultOut).DoesNotContain("src/A.cs");

        var (code, stdout, _) = await Run("report", dir, "--pattern", "**/coverage.xml");
        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stdout).Contains("src/A.cs");
    }

    [Test]
    public async Task Report_Pattern_TopLevelShape_DoesNotRecurse()
    {
        HalfCovered("toplevel/cobertura.xml");
        BranchHalf("toplevel/nested/cobertura.xml");

        var (code, stdout, _) = await Run(
            "report", _ws.PathOf("toplevel"), "--pattern", "cobertura.xml");

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stdout).Contains("src/A.cs");
        await Assert.That(stdout).DoesNotContain("src/B.cs");
    }

    [Test]
    public async Task Check_Pattern_GatesNonDefaultFilenames()
    {
        HalfCovered("chk/coverage.xml");

        var (code, _, stderr) = await Run(
            "check", _ws.PathOf("chk"), "--min-line", "90", "--pattern", "**/coverage.xml");

        await Assert.That(code).IsEqualTo(1);
        await Assert.That(stderr).Contains("FAIL");
    }

    [Test]
    public async Task Report_InvalidPattern_FriendlyError_Exits2()
    {
        // An invalid --pattern is rejected at the option boundary as a one-line CLI error,
        // never a stack trace.
        var dir = _ws.CreateDirectory("pat");

        var (code, _, stderr) = await Run("report", dir, "--pattern", "sub/coverage.xml");

        await Assert.That(code).IsEqualTo(2);
        await AssertFriendlyError(stderr);
        await Assert.That(stderr).Contains("Unsupported pattern");
    }

    // ── --max-chars ──

    [Test]
    public async Task Report_MaxChars_BelowDocumentSize_FriendlyError_Exits5()
    {
        var path = HalfCovered();

        var (code, _, stderr) = await Run("report", path, "--max-chars", "10");

        await Assert.That(code).IsEqualTo(5);
        await AssertFriendlyError(stderr);
        await Assert.That(stderr).Contains(path);
        await Assert.That(stderr).Contains("MaxCharactersInDocument");
    }

    [Test]
    public async Task Report_MaxChars_ZeroDisablesCap_Exits0()
    {
        var (code, _, _) = await Run("report", HalfCovered(), "--max-chars", "0");

        await Assert.That(code).IsEqualTo(0);
    }

    [Test]
    [Arguments("abc")]
    [Arguments("-1")]
    [Arguments("1.5")]
    public async Task Report_InvalidMaxChars_Exits2_AndNamesValue(string value)
    {
        var (code, _, stderr) = await Run("report", HalfCovered(), "--max-chars", value);

        await Assert.That(code).IsEqualTo(2);
        await Assert.That(stderr).Contains($"Invalid --max-chars value: '{value}'");
    }

    [Test]
    public async Task Check_MaxChars_CapOverflow_IsErrorNotFail()
    {
        // A size-cap overflow during check is a could-not-measure error ("error:" first token),
        // never presented as a coverage FAIL: it returns input/IO error 5, not threshold 1.
        var (code, _, stderr) = await Run(
            "check", HalfCovered(), "--min-line", "40", "--max-chars", "10");

        await Assert.That(code).IsEqualTo(5);
        await Assert.That(stderr).StartsWith("error:");
        await Assert.That(stderr).DoesNotContain("FAIL");
    }

    // ── Offender list scoping and flooring ──

    [Test]
    public async Task Check_LineFailure_LabelsOffenderList()
    {
        var (code, _, stderr) = await Run("check", HalfCovered(), "--min-line", "90");

        await Assert.That(code).IsEqualTo(1);
        await Assert.That(stderr).Contains("files below line threshold:" + Environment.NewLine + "  src/A.cs  50.0% lines");
    }

    [Test]
    public async Task Check_BranchOnlyFailure_ListsOnlyTheFilesBelowTheBranchThreshold()
    {
        // Line gate passes overall (5/7 ≈ 71.4% ≥ 50) but B.cs sits below min-line; the branch
        // gate fails. B.cs has zero failing branches and must not be blamed for the failure;
        // A.cs, whose branch is half covered, is.
        var path = _ws.Write("mixed.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c.Line(1, hits: 1).Line(2, hits: 1).Line(3, hits: 1).Branch(4, "50% (1/2)"))
            .AddClass("src/B.cs", c => c.Line(1, hits: 1).Line(2, hits: 0).Line(3, hits: 0))
            .ToBytes());

        var (code, _, stderr) = await Run("check", path, "--min-line", "50", "--min-branch", "90");

        await Assert.That(code).IsEqualTo(1);
        await Assert.That(stderr).Contains("FAIL");
        await Assert.That(stderr).Contains("branch coverage below threshold");
        await Assert.That(stderr).Contains("files below branch threshold:" + Environment.NewLine +
            "  src/A.cs  100.0% lines  50.0% branches" + Environment.NewLine + "    branches 4 (1/2)");
        await Assert.That(stderr).DoesNotContain("src/B.cs");
    }

    [Test]
    public async Task Check_NoData_PrintsNoOffenderList()
    {
        var empty = _ws.CreateDirectory("empty-nolist");

        var (code, _, stderr) = await Run("check", empty, "--min-line", "80");

        await Assert.That(code).IsEqualTo(3);
        await Assert.That(stderr).Contains("NODATA");
        await Assert.That(stderr).DoesNotContain("files below line threshold");
    }

    [Test]
    public async Task Check_OffenderList_FloorsFailingRate()
    {
        // 1999/2500 = 79.96%: must floor to 79.9%, never F1-round up to the missed minimum.
        var path = _ws.Write("floor.cobertura.xml", CoberturaSamples.JustBelowEightyPercent().ToBytes());

        var (code, _, stderr) = await Run("check", path, "--min-line", "80");

        await Assert.That(code).IsEqualTo(1);
        await Assert.That(stderr).Contains("  src/F.cs  79.9% lines");
        await Assert.That(stderr).DoesNotContain("80.0%");
    }

    // ── Unsupported upload scheme ──

    [Test]
    public async Task Report_UploadUnsupportedScheme_FriendlyError_Exits2()
    {
        // Unsupported upload schemes are usage errors before any IO.
        var (code, _, stderr) = await Run("report", HalfCovered(), "--upload", "ftp://example.invalid/x");

        await Assert.That(code).IsEqualTo(2);
        await Assert.That(stderr).Contains("Invalid --upload value: 'ftp://example.invalid/x'");
        await Assert.That(stderr).DoesNotContain("Unhandled exception");
    }

    // ── Snapshot identity defaults ──

    [Test]
    public async Task Snapshot_MissingIdentityFlags_WarnsButExits0()
    {
        var (code, stdout, stderr) = await Run("snapshot", HalfCovered());

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stdout).Contains("unknown");
        await Assert.That(stderr).Contains("warning: --commit, --branch, --project not provided; snapshot stamped 'unknown'");
    }

    [Test]
    public async Task Snapshot_PartialIdentityFlags_WarnsOnlyMissing()
    {
        var (code, _, stderr) = await Run("snapshot", HalfCovered(), "--commit", "abc123");

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stderr).Contains("--branch, --project not provided");
        await Assert.That(stderr).DoesNotContain("--commit");
    }

    [Test]
    public async Task Snapshot_AllIdentityFlags_NoWarning()
    {
        var (code, _, stderr) = await Run(
            "snapshot", HalfCovered(), "--commit", "abc123", "--branch", "main", "--project", "MyApp");

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stderr).DoesNotContain("warning:");
    }

    [Test]
    public async Task Snapshot_Directory_HashesTheMergedReports()
    {
        HalfCovered("snap/a/coverage.cobertura.xml");
        BranchHalf("snap/b/coverage.cobertura.xml");

        var (code, stdout, _) = await Run(
            "snapshot", _ws.PathOf("snap"), "--commit", "abc123", "--branch", "main", "--project", "MyApp");

        await Assert.That(code).IsEqualTo(0);
        var hash = System.Text.Json.JsonDocument.Parse(stdout).RootElement.GetProperty("fileHash").GetString();
        await Assert.That(hash).IsNotNull();
        await Assert.That(hash!.Length).IsEqualTo(64);
    }

    // ── Documented CLI contract ──

    [Test]
    public async Task Help_DocumentsExitCodesAndFlags()
    {
        var (code, stdout, _) = await Run("help");

        await Assert.That(code).IsEqualTo(0);
        // One table, every code once and in order.
        await Assert.That(stdout.Split("Exit codes:").Length).IsEqualTo(2);
        var table = stdout[stdout.IndexOf("Exit codes:", StringComparison.Ordinal)..];
        var at = 0;
        foreach (var row in new[]
                 {
                     "0  success;", "1  FAIL:", "2  usage error:", "3  NODATA:", "4  DISABLED:",
                     "5  input/IO error:", "6  test run failed:"
                 })
        {
            var next = table.IndexOf($"{Environment.NewLine}  {row}", at, StringComparison.Ordinal);
            await Assert.That(next).IsGreaterThanOrEqualTo(at).Because(row);
            at = next + 1;
        }
        await Assert.That(stdout).Contains("--pattern");
        await Assert.That(stdout).Contains("--max-chars");
        await Assert.That(stdout).Contains("--badge-dir <dir>");
        await Assert.That(stdout).Contains("--no-color");
        await Assert.That(stdout).Contains("NO_COLOR=1");
    }
}
