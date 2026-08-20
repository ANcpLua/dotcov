using DotCov.Tests.Infrastructure;
using DotCov.Tool;
using Xunit;

namespace DotCov.Tests;

/// <summary>
/// Pins the CLI contract of <see cref="DotCovCli"/>: exit codes, arg parsing, and error paths.
/// These ARE the CI semantics of the tool — a regression in any exit code silently changes
/// what every consumer's pipeline enforces.
/// </summary>
public sealed class CliTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("dotcov-cli-tests-");

    public void Dispose() => _dir.Delete(recursive: true);

    private static async Task<(int Code, string StdOut, string StdErr)> Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await DotCovCli.RunAsync(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    private string WriteFile(string relative, byte[] bytes)
    {
        var path = Path.Combine(_dir.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string WriteFile(string relative, string text) =>
        WriteFile(relative, System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>50% line coverage, no branch data.</summary>
    private string HalfCovered(string relative = "coverage.cobertura.xml") =>
        WriteFile(relative, Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c.Line(1, hits: 1).Line(2, hits: 0))
            .ToBytes());

    /// <summary>100% line coverage, 50% branch coverage (1/2).</summary>
    private string BranchHalf(string relative = "branch.cobertura.xml") =>
        WriteFile(relative, Cobertura.NewDoc()
            .AddClass("src/B.cs", c => c.Line(1, hits: 1).Branch(2, "50% (1/2)"))
            .ToBytes());

    // ── Exit-code matrix ──

    [Fact]
    public async Task Check_Pass_Exits0()
    {
        var (code, stdout, _) = await Run("check", HalfCovered(), "--min-line", "40");

        Assert.Equal(0, code);
        Assert.Contains("PASS", stdout);
    }

    [Fact]
    public async Task Check_Fail_Exits1_AndListsOffendingFiles()
    {
        var (code, _, stderr) = await Run("check", HalfCovered(), "--min-line", "90");

        Assert.Equal(1, code);
        Assert.Contains("FAIL", stderr);
        Assert.Contains("src/A.cs: 50.0%", stderr);
    }

    [Fact]
    public async Task Check_BranchBelowThreshold_Exits1()
    {
        var (code, _, stderr) = await Run("check", BranchHalf(), "--min-line", "50", "--min-branch", "90");

        Assert.Equal(1, code);
        Assert.Contains("FAIL", stderr);
        Assert.Contains("branch", stderr);
    }

    [Fact]
    public async Task Check_ZeroThresholds_Disabled_Exits1()
    {
        var (code, _, stderr) = await Run("check", HalfCovered(), "--min-line", "0");

        Assert.Equal(1, code);
        Assert.Contains("DISABLED", stderr);
    }

    [Fact]
    public async Task Check_EmptyDirectory_NoData_Exits1()
    {
        var empty = Directory.CreateDirectory(Path.Combine(_dir.FullName, "empty")).FullName;

        var (code, _, stderr) = await Run("check", empty, "--min-line", "80");

        Assert.Equal(1, code);
        Assert.Contains("NODATA", stderr);
    }

    // ── Unknown command / help ──

    [Fact]
    public async Task UnknownCommand_PrintsHelp_Exits2()
    {
        var (code, stdout, stderr) = await Run("chek", "whatever.xml", "--min-line", "80");

        Assert.Equal(2, code);
        Assert.Contains("Unknown command 'chek'", stderr);
        Assert.Contains("Commands:", stdout);
    }

    [Theory]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("-h")]
    public async Task ExplicitHelp_Exits0(string arg)
    {
        var (code, stdout, _) = await Run(arg);

        Assert.Equal(0, code);
        Assert.Contains("Commands:", stdout);
    }

    [Fact]
    public async Task NoArgs_PrintsHelp_Exits0()
    {
        var (code, stdout, _) = await Run();

        Assert.Equal(0, code);
        Assert.Contains("Commands:", stdout);
    }

    [Fact]
    public async Task Version_Exits0()
    {
        var (code, stdout, _) = await Run("version");

        Assert.Equal(0, code);
        Assert.StartsWith("dotcov ", stdout);
    }

    // ── Usage errors ──

    [Theory]
    [InlineData("report")]
    [InlineData("check")]
    [InlineData("diff")]
    [InlineData("snapshot")]
    public async Task MissingPathArgument_PrintsUsage_Exits1(string command)
    {
        var (code, _, stderr) = await Run(command);

        Assert.Equal(1, code);
        Assert.Contains("Usage:", stderr);
    }

    // ── Invalid numeric flags ──

    [Theory]
    [InlineData("--threshold", "abc")]
    [InlineData("--threshold", "NaN")]
    public async Task Report_InvalidThreshold_Exits1(string flag, string value)
    {
        var (code, _, stderr) = await Run("report", HalfCovered(), flag, value);

        Assert.Equal(1, code);
        Assert.Contains($"Invalid --threshold value: '{value}'", stderr);
    }

    [Theory]
    [InlineData("--min-line", "eighty")]
    [InlineData("--min-line", "NaN")]
    [InlineData("--min-branch", "5%")]
    [InlineData("--min-branch", "NaN")]
    public async Task Check_InvalidThreshold_Exits1_AndNamesValue(string flag, string value)
    {
        var (code, _, stderr) = await Run("check", HalfCovered(), flag, value);

        Assert.Equal(1, code);
        Assert.Contains($"Invalid {flag} value: '{value}'", stderr);
    }

    // ── --format validation ──

    [Fact]
    public async Task Report_InvalidFormat_Exits1()
    {
        var (code, _, stderr) = await Run("report", HalfCovered(), "--format", "yaml");

        Assert.Equal(1, code);
        Assert.Contains("Invalid --format value: 'yaml'", stderr);
    }

    [Fact]
    public async Task Diff_InvalidFormat_Exits1()
    {
        var path = HalfCovered();

        var (code, _, stderr) = await Run("diff", path, path, "--format", "jsn");

        Assert.Equal(1, code);
        Assert.Contains("Invalid --format value: 'jsn'", stderr);
    }

    [Theory]
    [InlineData("table")]
    [InlineData("json")]
    [InlineData("markdown")]
    [InlineData("md")]
    public async Task Report_KnownFormats_Exit0(string format)
    {
        var (code, stdout, _) = await Run("report", HalfCovered(), "--format", format);

        Assert.Equal(0, code);
        Assert.NotEmpty(stdout);
    }

    [Fact]
    public async Task Report_DefaultFormat_IsTable()
    {
        var (code, stdout, _) = await Run("report", HalfCovered());

        Assert.Equal(0, code);
        Assert.Contains("src/A.cs", stdout);
    }

    // ── Parse/IO failures: friendly one-liners, never stack traces ──

    private static void AssertFriendlyError(string stderr)
    {
        Assert.StartsWith("error:", stderr);
        Assert.DoesNotContain("Unhandled exception", stderr);
        Assert.DoesNotContain("   at ", stderr);
    }

    [Fact]
    public async Task Report_MalformedXml_FriendlyError_Exits1()
    {
        var path = WriteFile("truncated.xml", "<coverage><packages><package");

        var (code, _, stderr) = await Run("report", path);

        Assert.Equal(1, code);
        AssertFriendlyError(stderr);
        Assert.Contains(path, stderr);
    }

    [Fact]
    public async Task Report_EmptyFile_FriendlyError_Exits1()
    {
        var path = WriteFile("empty.xml", "");

        var (code, _, stderr) = await Run("report", path);

        Assert.Equal(1, code);
        AssertFriendlyError(stderr);
    }

    [Fact]
    public async Task Report_DoctypeHeader_ParsesSuccessfully_Exits0()
    {
        // Reference Cobertura always emits a DOCTYPE; the parser ignores the DTD without resolving it.
        var path = WriteFile("dtd.xml",
            """<?xml version="1.0"?><!DOCTYPE coverage [<!ENTITY x "y">]><coverage></coverage>""");

        var (code, _, _) = await Run("report", path);

        Assert.Equal(0, code);
    }

    [Fact]
    public async Task Report_NonexistentPath_FriendlyError_Exits1()
    {
        var (code, _, stderr) = await Run("report", "/no/such/path.xml");

        Assert.Equal(1, code);
        AssertFriendlyError(stderr);
        Assert.Contains("/no/such/path.xml", stderr);
    }

    [Fact]
    public async Task Diff_NonexistentSecondPath_FriendlyError_Exits1()
    {
        var (code, _, stderr) = await Run("diff", HalfCovered(), "/no/such/after.xml");

        Assert.Equal(1, code);
        AssertFriendlyError(stderr);
        Assert.Contains("/no/such/after.xml", stderr);
    }

    [Fact]
    public async Task Snapshot_MalformedXml_FriendlyError_Exits1()
    {
        var path = WriteFile("bad.xml", "<coverage>");

        var (code, _, stderr) = await Run("snapshot", path);

        Assert.Equal(1, code);
        AssertFriendlyError(stderr);
    }

    [Fact]
    public async Task DirectoryScan_MalformedFile_ErrorNamesOffendingFile()
    {
        HalfCovered("scan/a/coverage.cobertura.xml");
        var bad = WriteFile("scan/b/coverage.cobertura.xml", "<coverage><packa");

        var (code, _, stderr) = await Run("report", Path.Combine(_dir.FullName, "scan"));

        Assert.Equal(1, code);
        AssertFriendlyError(stderr);
        Assert.Contains(bad, stderr);
    }

    [Fact]
    public async Task DirectoryScan_AggregatesAllReports()
    {
        HalfCovered("agg/a/coverage.cobertura.xml");
        BranchHalf("agg/b/coverage.cobertura.xml");

        var (code, stdout, _) = await Run("report", Path.Combine(_dir.FullName, "agg"));

        Assert.Equal(0, code);
        Assert.Contains("src/A.cs", stdout);
        Assert.Contains("src/B.cs", stdout);
    }

    // ── Upload failures ──

    [Fact]
    public async Task Report_UploadMalformedUrl_FriendlyError_Exits1()
    {
        var (code, _, stderr) = await Run("report", HalfCovered(), "--upload", "notaurl");

        Assert.Equal(1, code);
        Assert.Contains("Upload failed: notaurl", stderr);
        Assert.DoesNotContain("Unhandled exception", stderr);
    }

    [Fact]
    public async Task Snapshot_UploadConnectionRefused_Exits1_AfterWritingJson()
    {
        var (code, stdout, stderr) = await Run(
            "snapshot", HalfCovered(), "--commit", "abc123", "--upload", "http://127.0.0.1:1/x");

        Assert.Equal(1, code);
        Assert.Contains("abc123", stdout);
        Assert.Contains("Upload failed: http://127.0.0.1:1/x", stderr);
        Assert.DoesNotContain("Unhandled exception", stderr);
    }

    [Fact]
    public async Task Check_PassingGate_UploadFailure_Exits1()
    {
        var (code, _, stderr) = await Run(
            "check", HalfCovered(), "--min-line", "40", "--upload", "http://127.0.0.1:1/x");

        Assert.Equal(1, code);
        Assert.Contains("Upload failed", stderr);
    }

    [Fact]
    public async Task Check_FailingGate_StillAttemptsUpload_Exits1()
    {
        // Failing runs are the ones a dashboard most needs; the upload is attempted and the
        // gate's exit 1 wins regardless of the upload outcome.
        var (code, _, stderr) = await Run(
            "check", HalfCovered(), "--min-line", "99", "--upload", "http://127.0.0.1:1/x");

        Assert.Equal(1, code);
        Assert.Contains("FAIL", stderr);
        Assert.Contains("Upload failed", stderr);
    }

    // ── ParseArgs mapping ──

    [Fact]
    public void ParseArgs_EmptyArgv_IsHelp()
    {
        var (command, options) = DotCovCli.ParseArgs([]);

        Assert.Equal("help", command);
        Assert.Empty(options);
    }

    [Fact]
    public void ParseArgs_PositionalMapsToFile()
    {
        var (command, options) = DotCovCli.ParseArgs(["report", "cov.xml"]);

        Assert.Equal("report", command);
        Assert.Equal("cov.xml", options["file"]);
    }

    [Fact]
    public void ParseArgs_DiffMapsPositionalsToBeforeAfter()
    {
        var (_, options) = DotCovCli.ParseArgs(["diff", "a.xml", "b.xml", "extra"]);

        Assert.Equal("a.xml", options["before"]);
        Assert.Equal("b.xml", options["after"]);
        Assert.Equal("extra", options["arg2"]);
    }

    [Fact]
    public void ParseArgs_FlagWithValue_AndValuelessFlags()
    {
        var (_, options) = DotCovCli.ParseArgs(
            ["check", "cov.xml", "--min-line", "80", "--exclude-generated", "--github-summary"]);

        Assert.Equal("cov.xml", options["file"]);
        Assert.Equal("80", options["min-line"]);
        Assert.Equal("true", options["exclude-generated"]);
        // Trailing flag with no value is recorded as "true", not dropped.
        Assert.Equal("true", options["github-summary"]);
    }

    [Fact]
    public void ParseArgs_BooleanFlagBeforePositional_DoesNotSwallowPath()
    {
        // Standard flag-before-path ordering: a valueless flag must not consume the path.
        var (_, options) = DotCovCli.ParseArgs(
            ["report", "--exclude-generated", "cov.xml", "--github-summary", "--format", "json"]);

        Assert.Equal("cov.xml", options["file"]);
        Assert.Equal("true", options["exclude-generated"]);
        Assert.Equal("true", options["github-summary"]);
        Assert.Equal("json", options["format"]);
    }

    [Fact]
    public async Task Report_BooleanFlagBeforePath_Exits0()
    {
        var (code, stdout, _) = await Run("report", "--exclude-generated", HalfCovered());

        Assert.Equal(0, code);
        Assert.Contains("src/A.cs", stdout);
    }

    // ── Directory-scan error attribution ──

    [Fact]
    public async Task DirectoryScan_MalformedFile_PrefixesPathExactlyOnce()
    {
        // The library's ParseFile prefixes the failing path; the CLI must not prefix again
        // ("error: {path}: {path}: ...").
        var bad = WriteFile("once/coverage.cobertura.xml", "<coverage><packa");

        var (code, _, stderr) = await Run("report", Path.Combine(_dir.FullName, "once"));

        Assert.Equal(1, code);
        Assert.Contains($"error: {bad}:", stderr);
        Assert.DoesNotContain($"{bad}: {bad}:", stderr);
    }

    // ── --pattern ──

    [Fact]
    public async Task Report_Pattern_DiscoversNonDefaultFilenames()
    {
        HalfCovered("gcovr/sub/coverage.xml");
        var dir = Path.Combine(_dir.FullName, "gcovr");

        // Default scan only matches **/coverage.cobertura.xml — the gcovr-named report is invisible.
        var (defaultCode, defaultOut, _) = await Run("report", dir);
        Assert.Equal(0, defaultCode);
        Assert.DoesNotContain("src/A.cs", defaultOut);

        var (code, stdout, _) = await Run("report", dir, "--pattern", "**/coverage.xml");
        Assert.Equal(0, code);
        Assert.Contains("src/A.cs", stdout);
    }

    [Fact]
    public async Task Report_Pattern_TopLevelShape_DoesNotRecurse()
    {
        HalfCovered("toplevel/cobertura.xml");
        BranchHalf("toplevel/nested/cobertura.xml");

        var (code, stdout, _) = await Run(
            "report", Path.Combine(_dir.FullName, "toplevel"), "--pattern", "cobertura.xml");

        Assert.Equal(0, code);
        Assert.Contains("src/A.cs", stdout);
        Assert.DoesNotContain("src/B.cs", stdout);
    }

    [Fact]
    public async Task Check_Pattern_GatesNonDefaultFilenames()
    {
        HalfCovered("chk/coverage.xml");

        var (code, _, stderr) = await Run(
            "check", Path.Combine(_dir.FullName, "chk"), "--min-line", "90", "--pattern", "**/coverage.xml");

        Assert.Equal(1, code);
        Assert.Contains("FAIL", stderr);
    }

    [Fact]
    public async Task Report_InvalidPattern_FriendlyError_Exits1()
    {
        // ParseDirectory's pattern gate throws ArgumentException, which is not in RunAsync's
        // catch filter — the CLI must translate it, not crash with a stack trace.
        var dir = Directory.CreateDirectory(Path.Combine(_dir.FullName, "pat")).FullName;

        var (code, _, stderr) = await Run("report", dir, "--pattern", "sub/coverage.xml");

        Assert.Equal(1, code);
        AssertFriendlyError(stderr);
        Assert.Contains("Unsupported pattern", stderr);
    }

    // ── --max-chars ──

    [Fact]
    public async Task Report_MaxChars_BelowDocumentSize_FriendlyError_Exits1()
    {
        var path = HalfCovered();

        var (code, _, stderr) = await Run("report", path, "--max-chars", "10");

        Assert.Equal(1, code);
        AssertFriendlyError(stderr);
        Assert.Contains(path, stderr);
        Assert.Contains("MaxCharactersInDocument", stderr);
    }

    [Fact]
    public async Task Report_MaxChars_ZeroDisablesCap_Exits0()
    {
        var (code, _, _) = await Run("report", HalfCovered(), "--max-chars", "0");

        Assert.Equal(0, code);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("1.5")]
    public async Task Report_InvalidMaxChars_Exits1_AndNamesValue(string value)
    {
        var (code, _, stderr) = await Run("report", HalfCovered(), "--max-chars", value);

        Assert.Equal(1, code);
        Assert.Contains($"Invalid --max-chars value: '{value}'", stderr);
    }

    [Fact]
    public async Task Check_MaxChars_CapOverflow_IsErrorNotFail()
    {
        // A size-cap overflow during check is a could-not-measure error ("error:" first token),
        // never presented as a coverage FAIL — the exit code is 1 either way (documented
        // contract), so the stderr first token is the only discriminator.
        var (code, _, stderr) = await Run(
            "check", HalfCovered(), "--min-line", "40", "--max-chars", "10");

        Assert.Equal(1, code);
        Assert.StartsWith("error:", stderr);
        Assert.DoesNotContain("FAIL", stderr);
    }

    // ── Offender list scoping and flooring ──

    [Fact]
    public async Task Check_LineFailure_LabelsOffenderList()
    {
        var (code, _, stderr) = await Run("check", HalfCovered(), "--min-line", "90");

        Assert.Equal(1, code);
        Assert.Contains("files below line threshold:", stderr);
        Assert.Contains("src/A.cs: 50.0%", stderr);
    }

    [Fact]
    public async Task Check_BranchOnlyFailure_PrintsNoLineOffenderList()
    {
        // Line gate passes overall (5/7 ≈ 71.4% ≥ 50) but B.cs sits below min-line; the branch
        // gate fails. B.cs has zero failing branches and must not be blamed for the failure.
        var path = WriteFile("mixed.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c.Line(1, hits: 1).Line(2, hits: 1).Line(3, hits: 1).Branch(4, "50% (1/2)"))
            .AddClass("src/B.cs", c => c.Line(1, hits: 1).Line(2, hits: 0).Line(3, hits: 0))
            .ToBytes());

        var (code, _, stderr) = await Run("check", path, "--min-line", "50", "--min-branch", "90");

        Assert.Equal(1, code);
        Assert.Contains("FAIL", stderr);
        Assert.Contains("branch coverage below threshold", stderr);
        Assert.DoesNotContain("files below line threshold", stderr);
        Assert.DoesNotContain("src/B.cs", stderr);
    }

    [Fact]
    public async Task Check_NoData_PrintsNoOffenderList()
    {
        var empty = Directory.CreateDirectory(Path.Combine(_dir.FullName, "empty-nolist")).FullName;

        var (code, _, stderr) = await Run("check", empty, "--min-line", "80");

        Assert.Equal(1, code);
        Assert.Contains("NODATA", stderr);
        Assert.DoesNotContain("files below line threshold", stderr);
    }

    [Fact]
    public async Task Check_OffenderList_FloorsFailingRate()
    {
        // 1999/2500 = 79.96%: must floor to 79.9%, never F1-round up to the missed minimum.
        var path = WriteFile("floor.cobertura.xml", NearlyEighty().ToBytes());

        var (code, _, stderr) = await Run("check", path, "--min-line", "80");

        Assert.Equal(1, code);
        Assert.Contains("src/F.cs: 79.9%", stderr);
        Assert.DoesNotContain("80.0%", stderr);
    }

    internal static Cobertura NearlyEighty() => Cobertura.NewDoc()
        .AddClass("src/F.cs", c =>
        {
            for (var i = 1; i <= 1999; i++) c.Line(i, hits: 1);
            for (var i = 2000; i <= 2500; i++) c.Line(i, hits: 0);
        });

    // ── Unsupported upload scheme ──

    [Fact]
    public async Task Report_UploadUnsupportedScheme_FriendlyError_Exits1()
    {
        // HttpClient throws NotSupportedException for non-http(s) schemes before any
        // connection is attempted — must be a one-liner, not an unhandled crash.
        var (code, _, stderr) = await Run("report", HalfCovered(), "--upload", "ftp://example.invalid/x");

        Assert.Equal(1, code);
        Assert.Contains("Upload failed: ftp://example.invalid/x", stderr);
        Assert.DoesNotContain("Unhandled exception", stderr);
    }

    // ── Snapshot identity defaults ──

    [Fact]
    public async Task Snapshot_MissingIdentityFlags_WarnsButExits0()
    {
        var (code, stdout, stderr) = await Run("snapshot", HalfCovered());

        Assert.Equal(0, code);
        Assert.Contains("unknown", stdout);
        Assert.Contains(
            "warning: --commit, --branch, --project not provided; snapshot stamped 'unknown'", stderr);
    }

    [Fact]
    public async Task Snapshot_PartialIdentityFlags_WarnsOnlyMissing()
    {
        var (code, _, stderr) = await Run("snapshot", HalfCovered(), "--commit", "abc123");

        Assert.Equal(0, code);
        Assert.Contains("--branch, --project not provided", stderr);
        Assert.DoesNotContain("--commit", stderr);
    }

    [Fact]
    public async Task Snapshot_AllIdentityFlags_NoWarning()
    {
        var (code, _, stderr) = await Run(
            "snapshot", HalfCovered(), "--commit", "abc123", "--branch", "main", "--project", "MyApp");

        Assert.Equal(0, code);
        Assert.DoesNotContain("warning:", stderr);
    }

    // ── Documented CLI contract ──

    [Fact]
    public async Task Help_DocumentsExitCodesAndNewFlags()
    {
        var (code, stdout, _) = await Run("help");

        Assert.Equal(0, code);
        Assert.Contains("Exit codes:", stdout);
        Assert.Contains("2  unknown command", stdout);
        Assert.Contains("--pattern", stdout);
        Assert.Contains("--max-chars", stdout);
    }
}
