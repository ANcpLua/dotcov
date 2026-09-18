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
    public async Task Check_Pass_Exits0()
    {
        var (code, stdout, _) = await Run("check", HalfCovered(), "--min-line", "40");

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stdout).Contains("PASS");
    }

    [Test]
    public async Task Check_MtpDirectoryWithoutPattern_FindsTimestampedReport()
    {
        HalfCovered("run/dotcov.coverage.cobertura.170926234734218.xml");

        var (code, stdout, stderr) = await Run("check", _ws.Root, "--min-line", "40");

        await Assert.That(code).IsEqualTo(0).Because(stderr);
        await Assert.That(stdout).Contains("PASS: line 50.0%");
    }

    [Test]
    public async Task Check_Fail_Exits1_AndListsOffendingFiles()
    {
        var (code, _, stderr) = await Run("check", HalfCovered(), "--min-line", "90");

        await Assert.That(code).IsEqualTo(1);
        await Assert.That(stderr).Contains("FAIL");
        await Assert.That(stderr).Contains("src/A.cs: 50.0%");
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
    public async Task Check_ZeroThresholds_Disabled_Exits1()
    {
        var (code, _, stderr) = await Run("check", HalfCovered(), "--min-line", "0");

        await Assert.That(code).IsEqualTo(1);
        await Assert.That(stderr).Contains("DISABLED");
    }

    [Test]
    public async Task Check_EmptyDirectory_NoData_Exits1()
    {
        var empty = _ws.CreateDirectory("empty");

        var (code, _, stderr) = await Run("check", empty, "--min-line", "80");

        await Assert.That(code).IsEqualTo(1);
        await Assert.That(stderr).Contains("NODATA");
    }

    // ── Unknown command / help ──

    [Test]
    public async Task UnknownCommand_PrintsHelp_Exits2()
    {
        var (code, stdout, stderr) = await Run("chek", "whatever.xml", "--min-line", "80");

        await Assert.That(code).IsEqualTo(2);
        await Assert.That(stderr).Contains("Unknown command 'chek'");
        await Assert.That(stdout).Contains("Commands:");
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
    public async Task Version_Exits0()
    {
        var (code, stdout, _) = await Run("version");

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stdout).StartsWith("dotcov ");
    }

    // ── Usage errors ──

    [Test]
    [Arguments("report")]
    [Arguments("check")]
    [Arguments("diff")]
    [Arguments("snapshot")]
    public async Task MissingPathArgument_PrintsUsage_Exits1(string command)
    {
        var (code, _, stderr) = await Run(command);

        await Assert.That(code).IsEqualTo(1);
        await Assert.That(stderr).Contains("Usage:");
    }

    // ── Invalid numeric flags ──

    [Test]
    [Arguments("--threshold", "abc")]
    [Arguments("--threshold", "NaN")]
    public async Task Report_InvalidThreshold_Exits1(string flag, string value)
    {
        var (code, _, stderr) = await Run("report", HalfCovered(), flag, value);

        await Assert.That(code).IsEqualTo(1);
        await Assert.That(stderr).Contains($"Invalid --threshold value: '{value}'");
    }

    [Test]
    [Arguments("--min-line", "eighty")]
    [Arguments("--min-line", "NaN")]
    [Arguments("--min-branch", "5%")]
    [Arguments("--min-branch", "NaN")]
    public async Task Check_InvalidThreshold_Exits1_AndNamesValue(string flag, string value)
    {
        var (code, _, stderr) = await Run("check", HalfCovered(), flag, value);

        await Assert.That(code).IsEqualTo(1);
        await Assert.That(stderr).Contains($"Invalid {flag} value: '{value}'");
    }

    // ── --format validation ──

    [Test]
    public async Task Report_InvalidFormat_Exits1()
    {
        var (code, _, stderr) = await Run("report", HalfCovered(), "--format", "yaml");

        await Assert.That(code).IsEqualTo(1);
        await Assert.That(stderr).Contains("Invalid --format value: 'yaml'");
    }

    [Test]
    public async Task Diff_InvalidFormat_Exits1()
    {
        var path = HalfCovered();

        var (code, _, stderr) = await Run("diff", path, path, "--format", "jsn");

        await Assert.That(code).IsEqualTo(1);
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
    public async Task Report_MalformedXml_FriendlyError_Exits1()
    {
        var path = _ws.Write("truncated.xml", "<coverage><packages><package");

        var (code, _, stderr) = await Run("report", path);

        await Assert.That(code).IsEqualTo(1);
        await AssertFriendlyError(stderr);
        await Assert.That(stderr).Contains(path);
    }

    [Test]
    public async Task Report_EmptyFile_FriendlyError_Exits1()
    {
        var path = _ws.Write("empty.xml", "");

        var (code, _, stderr) = await Run("report", path);

        await Assert.That(code).IsEqualTo(1);
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
    public async Task Report_NonexistentPath_FriendlyError_Exits1()
    {
        var (code, _, stderr) = await Run("report", "/no/such/path.xml");

        await Assert.That(code).IsEqualTo(1);
        await AssertFriendlyError(stderr);
        await Assert.That(stderr).Contains("/no/such/path.xml");
    }

    [Test]
    public async Task Diff_NonexistentSecondPath_FriendlyError_Exits1()
    {
        var (code, _, stderr) = await Run("diff", HalfCovered(), "/no/such/after.xml");

        await Assert.That(code).IsEqualTo(1);
        await AssertFriendlyError(stderr);
        await Assert.That(stderr).Contains("/no/such/after.xml");
    }

    [Test]
    public async Task Snapshot_MalformedXml_FriendlyError_Exits1()
    {
        var path = _ws.Write("bad.xml", "<coverage>");

        var (code, _, stderr) = await Run("snapshot", path);

        await Assert.That(code).IsEqualTo(1);
        await AssertFriendlyError(stderr);
    }

    [Test]
    public async Task DirectoryScan_MalformedFile_ErrorNamesOffendingFile()
    {
        HalfCovered("scan/a/coverage.cobertura.xml");
        var bad = _ws.Write("scan/b/coverage.cobertura.xml", "<coverage><packa");

        var (code, _, stderr) = await Run("report", _ws.PathOf("scan"));

        await Assert.That(code).IsEqualTo(1);
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

    // ── Upload failures ──

    [Test]
    public async Task Report_UploadMalformedUrl_FriendlyError_Exits1()
    {
        var (code, _, stderr) = await Run("report", HalfCovered(), "--upload", "notaurl");

        await Assert.That(code).IsEqualTo(1);
        await Assert.That(stderr).Contains("Upload failed: notaurl");
        await Assert.That(stderr).DoesNotContain("Unhandled exception");
    }

    [Test]
    public async Task Snapshot_UploadConnectionRefused_Exits1_AfterWritingJson()
    {
        var (code, stdout, stderr) = await Run(
            "snapshot", HalfCovered(), "--commit", "abc123", "--upload", "http://127.0.0.1:1/x");

        await Assert.That(code).IsEqualTo(1);
        await Assert.That(stdout).Contains("abc123");
        await Assert.That(stderr).Contains("Upload failed: http://127.0.0.1:1/x");
        await Assert.That(stderr).DoesNotContain("Unhandled exception");
    }

    [Test]
    public async Task Check_PassingGate_UploadFailure_Exits1()
    {
        var (code, _, stderr) = await Run(
            "check", HalfCovered(), "--min-line", "40", "--upload", "http://127.0.0.1:1/x");

        await Assert.That(code).IsEqualTo(1);
        await Assert.That(stderr).Contains("Upload failed");
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

    // ── ParseArgs mapping ──

    [Test]
    public async Task ParseArgs_EmptyArgv_IsHelp()
    {
        var (command, options) = DotCovCli.ParseArgs([]);

        await Assert.That(command).IsEqualTo("help");
        await Assert.That(options).IsEmpty();
    }

    [Test]
    public async Task ParseArgs_PositionalMapsToFile()
    {
        var (command, options) = DotCovCli.ParseArgs(["report", "cov.xml"]);

        await Assert.That(command).IsEqualTo("report");
        await Assert.That(options["file"]).IsEqualTo("cov.xml");
    }

    [Test]
    public async Task ParseArgs_DiffMapsPositionalsToBeforeAfter()
    {
        var (_, options) = DotCovCli.ParseArgs(["diff", "a.xml", "b.xml", "extra"]);

        await Assert.That(options["before"]).IsEqualTo("a.xml");
        await Assert.That(options["after"]).IsEqualTo("b.xml");
        await Assert.That(options["arg2"]).IsEqualTo("extra");
    }

    [Test]
    public async Task ParseArgs_FlagWithValue_AndValuelessFlags()
    {
        var (_, options) = DotCovCli.ParseArgs(
            ["check", "cov.xml", "--min-line", "80", "--exclude-generated", "--github-summary"]);

        await Assert.That(options["file"]).IsEqualTo("cov.xml");
        await Assert.That(options["min-line"]).IsEqualTo("80");
        await Assert.That(options["exclude-generated"]).IsEqualTo("true");
        // Trailing flag with no value is recorded as "true", not dropped.
        await Assert.That(options["github-summary"]).IsEqualTo("true");
    }

    [Test]
    public async Task ParseArgs_BooleanFlagBeforePositional_DoesNotSwallowPath()
    {
        // Standard flag-before-path ordering: a valueless flag must not consume the path.
        var (_, options) = DotCovCli.ParseArgs(
            ["report", "--exclude-generated", "cov.xml", "--github-summary", "--format", "json"]);

        await Assert.That(options["file"]).IsEqualTo("cov.xml");
        await Assert.That(options["exclude-generated"]).IsEqualTo("true");
        await Assert.That(options["github-summary"]).IsEqualTo("true");
        await Assert.That(options["format"]).IsEqualTo("json");
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

        await Assert.That(code).IsEqualTo(1);
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
    public async Task Report_InvalidPattern_FriendlyError_Exits1()
    {
        // An invalid --pattern is rejected at the option boundary as a one-line CLI error,
        // never a stack trace.
        var dir = _ws.CreateDirectory("pat");

        var (code, _, stderr) = await Run("report", dir, "--pattern", "sub/coverage.xml");

        await Assert.That(code).IsEqualTo(1);
        await AssertFriendlyError(stderr);
        await Assert.That(stderr).Contains("Unsupported pattern");
    }

    // ── --max-chars ──

    [Test]
    public async Task Report_MaxChars_BelowDocumentSize_FriendlyError_Exits1()
    {
        var path = HalfCovered();

        var (code, _, stderr) = await Run("report", path, "--max-chars", "10");

        await Assert.That(code).IsEqualTo(1);
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
    public async Task Report_InvalidMaxChars_Exits1_AndNamesValue(string value)
    {
        var (code, _, stderr) = await Run("report", HalfCovered(), "--max-chars", value);

        await Assert.That(code).IsEqualTo(1);
        await Assert.That(stderr).Contains($"Invalid --max-chars value: '{value}'");
    }

    [Test]
    public async Task Check_MaxChars_CapOverflow_IsErrorNotFail()
    {
        // A size-cap overflow during check is a could-not-measure error ("error:" first token),
        // never presented as a coverage FAIL — the exit code is 1 either way (documented
        // contract), so the stderr first token is the only discriminator.
        var (code, _, stderr) = await Run(
            "check", HalfCovered(), "--min-line", "40", "--max-chars", "10");

        await Assert.That(code).IsEqualTo(1);
        await Assert.That(stderr).StartsWith("error:");
        await Assert.That(stderr).DoesNotContain("FAIL");
    }

    // ── Offender list scoping and flooring ──

    [Test]
    public async Task Check_LineFailure_LabelsOffenderList()
    {
        var (code, _, stderr) = await Run("check", HalfCovered(), "--min-line", "90");

        await Assert.That(code).IsEqualTo(1);
        await Assert.That(stderr).Contains("files below line threshold:");
        await Assert.That(stderr).Contains("src/A.cs: 50.0%");
    }

    [Test]
    public async Task Check_BranchOnlyFailure_PrintsNoLineOffenderList()
    {
        // Line gate passes overall (5/7 ≈ 71.4% ≥ 50) but B.cs sits below min-line; the branch
        // gate fails. B.cs has zero failing branches and must not be blamed for the failure.
        var path = _ws.Write("mixed.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c.Line(1, hits: 1).Line(2, hits: 1).Line(3, hits: 1).Branch(4, "50% (1/2)"))
            .AddClass("src/B.cs", c => c.Line(1, hits: 1).Line(2, hits: 0).Line(3, hits: 0))
            .ToBytes());

        var (code, _, stderr) = await Run("check", path, "--min-line", "50", "--min-branch", "90");

        await Assert.That(code).IsEqualTo(1);
        await Assert.That(stderr).Contains("FAIL");
        await Assert.That(stderr).Contains("branch coverage below threshold");
        await Assert.That(stderr).DoesNotContain("files below line threshold");
        await Assert.That(stderr).DoesNotContain("src/B.cs");
    }

    [Test]
    public async Task Check_NoData_PrintsNoOffenderList()
    {
        var empty = _ws.CreateDirectory("empty-nolist");

        var (code, _, stderr) = await Run("check", empty, "--min-line", "80");

        await Assert.That(code).IsEqualTo(1);
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
        await Assert.That(stderr).Contains("src/F.cs: 79.9%");
        await Assert.That(stderr).DoesNotContain("80.0%");
    }

    // ── Unsupported upload scheme ──

    [Test]
    public async Task Report_UploadUnsupportedScheme_FriendlyError_Exits1()
    {
        // HttpClient throws NotSupportedException for non-http(s) schemes before any
        // connection is attempted — must be a one-liner, not an unhandled crash.
        var (code, _, stderr) = await Run("report", HalfCovered(), "--upload", "ftp://example.invalid/x");

        await Assert.That(code).IsEqualTo(1);
        await Assert.That(stderr).Contains("Upload failed: ftp://example.invalid/x");
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

    // ── Documented CLI contract ──

    [Test]
    public async Task Help_DocumentsExitCodesAndNewFlags()
    {
        var (code, stdout, _) = await Run("help");

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stdout).Contains("Exit codes:");
        await Assert.That(stdout).Contains("2  unknown command");
        await Assert.That(stdout).Contains("--pattern");
        await Assert.That(stdout).Contains("--max-chars");
    }
}
