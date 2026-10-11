using System.Runtime.Versioning;
using System.Text.Json;
using DotCov.Tests.Infrastructure;
using DotCov.Tool;
using TUnit.Core.Enums;

namespace DotCov.Tests;

/// <summary>
/// Edge cases of the <see cref="DotCovCli"/> contract driven through <see cref="DotCovCli.RunAsync"/>:
/// empty and value-less arguments, the error advice the CLI prints, every failure type the CLI
/// promises to report as a one-line <c>error:</c>, and the flag paths each command shares.
/// </summary>
public sealed class DotCovCliEdgeTests : IDisposable
{
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-cli-edge-");

    public void Dispose() => _ws.Dispose();

    private static async Task<(int Code, string StdOut, string StdErr)> Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await DotCovCli.RunAsync(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    /// <summary>50% line coverage, no branch data.</summary>
    private string HalfCovered(string relative = "half.cobertura.xml") =>
        _ws.Write(relative, Cobertura.NewDoc().AddClass("src/A.cs", c => c.Line(1, hits: 1).Line(2, hits: 0)));

    private static bool CanOpen(string path)
    {
        try
        {
            File.OpenRead(path).Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task AssertOneLineError(int code, string stdout, string stderr, string expectedStart, int expectedCode = 2)
    {
        await Assert.That(code).IsEqualTo(expectedCode);
        await Assert.That(stdout).IsEmpty();
        await Assert.That(stderr).StartsWith(expectedStart);
        await Assert.That(stderr).DoesNotContain("   at ");
    }

    // ── An empty path argument is a missing path, never a crash ──

    [Test]
    [Arguments("report")]
    [Arguments("check")]
    [Arguments("crap")]
    [Arguments("snapshot")]
    public async Task EmptyPath_IsAMissingPathError_NotAnUnhandledException(string command)
    {
        // `dotcov check "$COVERAGE_DIR"` with the variable unset passes "". It gets the
        // documented exit 2 for a missing path, not an unhandled crash with a stack trace.
        var (code, stdout, stderr) = await Run(command, "");

        await AssertOneLineError(code, stdout, stderr, "error: missing path");
    }

    [Test]
    public async Task Diff_EmptyBeforeOrAfter_IsAMissingPathError()
    {
        var path = HalfCovered();

        var (beforeCode, beforeOut, beforeErr) = await Run("diff", "", path);
        var (afterCode, afterOut, afterErr) = await Run("diff", path, "");

        await AssertOneLineError(beforeCode, beforeOut, beforeErr, "error: missing path");
        await AssertOneLineError(afterCode, afterOut, afterErr, "error: missing path");
    }

    [Test]
    [Arguments("--metrics")]
    [Arguments("--METRICS")]
    public async Task EmptyMetricsPath_IsUsageRegardlessOfFlagCasing(string flag)
    {
        var (code, stdout, stderr) = await Run("crap", HalfCovered(), flag, "");

        await AssertOneLineError(code, stdout, stderr, "error: missing path");
    }

    [Test]
    public async Task ShortUnknownFlag_IsUsageBeforeReadingInput()
    {
        var (code, stdout, stderr) = await Run("report", "-q");

        await AssertOneLineError(code, stdout, stderr, "error: Unknown option '-q'");
    }

    [Test]
    public async Task HttpsUpload_WithMissingReport_IsExecutionFailure()
    {
        var (code, stdout, stderr) = await Run("report", _ws.PathOf("missing.xml"), "--upload", "https://example.invalid/coverage");

        await AssertOneLineError(code, stdout, stderr, "error: No file or directory", expectedCode: 5);
    }

    // ── A flag that takes a value but got none is an error, never the value "true" ──

    [Test]
    public async Task Snapshot_IdentityFlagWithoutValue_IsAnError_NotStampedTrue()
    {
        // An unquoted, empty `--commit $GIT_SHA` reaches dotcov as `--commit --branch main`.
        var (code, stdout, stderr) = await Run(
            "snapshot", HalfCovered(), "--commit", "--branch", "main", "--project", "app");

        await AssertOneLineError(code, stdout, stderr, "error: Option '--commit' requires a value.");
    }

    [Test]
    public async Task Report_TrailingPatternWithoutValue_IsAnError_NotASilentEmptyReport()
    {
        // Taken as a value, the "true" placeholder would scan for a file literally named "true".
        HalfCovered("dir/coverage.cobertura.xml");

        var (code, stdout, stderr) = await Run("report", _ws.PathOf("dir"), "--pattern");

        await AssertOneLineError(code, stdout, stderr, "error: Option '--pattern' requires a value.");
    }

    [Test]
    public async Task Check_KeepWithoutValue_IsAnError_NotAKeepRuleForTrue()
    {
        var (code, stdout, stderr) = await Run(
            "check", HalfCovered(), "--exclude-generated", "--keep", "--min-line", "40");

        await AssertOneLineError(code, stdout, stderr, "error: Option '--keep' requires a value.");
    }

    [Test]
    public async Task Check_ThresholdWithoutValue_NamesTheMissingValue_NotAnInvalidTrue()
    {
        var (code, stdout, stderr) = await Run("check", HalfCovered(), "--min-line");

        await AssertOneLineError(code, stdout, stderr, "error: Option '--min-line' requires a value.");
        await Assert.That(stderr).DoesNotContain("'true'");
    }

    [Test]
    public async Task UnknownFlagWithoutValue_IsStillReportedAsUnknown()
    {
        // The unknown name is the more useful diagnosis than its missing value.
        var (code, stdout, stderr) = await Run("check", HalfCovered(), "--verbose", "--min-line", "40");

        await AssertOneLineError(code, stdout, stderr, "error: Unknown option '--verbose' for 'check'.");
    }

    // ── A path is positional: the slot it is filed under is not a flag ──

    [Test]
    public async Task Check_FileFlag_IsAnError_NotASilentReplacementOfThePath()
    {
        // `--file` is in no help text. Accepting it would replace the positional path, so this
        // run would gate the passing report instead of the failing one.
        var failing = HalfCovered("failing.cobertura.xml");
        var passing = _ws.Write("passing.cobertura.xml", Cobertura.NewDoc().AddClass("src/B.cs", c => c.Line(1, hits: 1)));

        var (code, stdout, stderr) = await Run("check", failing, "--file", passing, "--min-line", "80");

        await AssertOneLineError(code, stdout, stderr,
            "error: Unknown option '--file' for 'check'. Pass the path as a positional argument.");
    }

    [Test]
    [Arguments("--before")]
    [Arguments("--AFTER")]
    public async Task Diff_PathSlotFlag_IsAnError_NotASilentReplacementOfThePath(string flag)
    {
        var path = HalfCovered();
        var other = _ws.Write("other.cobertura.xml", Cobertura.NewDoc().AddClass("src/B.cs", c => c.Line(1, hits: 1)));

        var (code, stdout, stderr) = await Run("diff", path, path, flag, other);

        await AssertOneLineError(code, stdout, stderr,
            $"error: Unknown option '{flag}' for 'diff'. Pass the path as a positional argument.");
    }

    // ── The advice in an error message must work when followed ──

    [Test]
    [Arguments("--exclude-generated=false")]
    [Arguments("--Exclude-Generated=true")]
    public async Task ValuelessFlagWithEqualsValue_AdvisesTheFlagAlone(string option)
    {
        // These flags never take a value: advice to write '--exclude-generated false' would
        // fail again with "Unexpected argument 'false'".
        var flag = option[..option.IndexOf('=')];

        var (code, stdout, stderr) = await Run("check", HalfCovered(), "--min-line", "40", option);
        var (adviceCode, _, adviceErr) = await Run("check", HalfCovered(), "--min-line", "40", flag);

        await AssertOneLineError(code, stdout, stderr,
            $"error: Unknown option '{option}' for 'check'. '{flag}' takes no value.");
        await Assert.That(stderr).DoesNotContain("Write ");
        await Assert.That(adviceCode).IsEqualTo(0).Because(adviceErr);
    }

    [Test]
    public async Task ValueFlagWithEqualsValue_AdvisesTheSpaceSeparatedForm_ThatWorks()
    {
        var (code, stdout, stderr) = await Run("check", HalfCovered(), "--min-line=40");
        var (adviceCode, adviceOut, _) = await Run("check", HalfCovered(), "--min-line", "40");

        await AssertOneLineError(code, stdout, stderr,
            "error: Unknown option '--min-line=40' for 'check'. Write '--min-line 40'.");
        await Assert.That(adviceCode).IsEqualTo(0);
        await Assert.That(adviceOut).Contains("PASS: line 50.0% (min 40%)");
    }

    // ── Every failure type RunAsync admits becomes one "error:" line; anything else propagates ──

    [Test]
    public async Task Crap_MalformedMetricsFile_IsAnErrorNamingTheFile()
    {
        // XmlException from the metrics reader, which prefixes the path.
        var coverage = _ws.Write("crap.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/A.cs", "MyApp.A", c => c.Method("M", "()", null, m => m.Line(1, hits: 1))));
        var metrics = _ws.Write("metrics.xml", "<CodeMetricsReport><Targets>");

        var (code, stdout, stderr) = await Run("crap", coverage, "--metrics", metrics);

        await AssertOneLineError(code, stdout, stderr, $"error: {metrics}: ", expectedCode: 5);
    }

    [Test]
    [RunOn(OS.Linux | OS.MacOs)]
    [UnsupportedOSPlatform("windows")]
    public async Task Report_FileThatCannotBeRead_IsAnError_NotACrash()
    {
        // UnauthorizedAccessException: a file without permissions refuses to open, except for root,
        // which reads through file modes. A root run uses a write-only sysfs attribute instead:
        // kernfs refuses to open it for reading with EACCES, even for root.
        var unreadable = _ws.Write("unreadable.cobertura.xml", "<coverage/>");
        File.SetUnixFileMode(unreadable, UnixFileMode.None);
        if (CanOpen(unreadable))
        {
            unreadable = Directory.Exists("/sys/bus")
                ? Directory.EnumerateDirectories("/sys/bus").Select(bus => Path.Combine(bus, "uevent")).FirstOrDefault(File.Exists)
                : null;
        }
        Skip.When(unreadable is null, "running as root on a host with no write-only sysfs attribute");

        var (code, stdout, stderr) = await Run("report", unreadable!);

        await AssertOneLineError(code, stdout, stderr, $"error: Access to the path '{unreadable}' is denied.", expectedCode: 5);
    }

    [Test]
    public async Task ProgrammingError_IsNotReportedAsACliFailure()
    {
        // Only the expected failure modes become "error:" lines; a writer the caller already
        // disposed is a bug in the caller and must surface as one.
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        await stdout.DisposeAsync();

        await Assert.That(async () => await DotCovCli.RunAsync(["version"], stdout, stderr))
            .ThrowsExactly<ObjectDisposedException>();
        await Assert.That(stderr.ToString()).IsEmpty();
    }

    // ── A machine-readable format stays machine-readable on every verdict ──

    [Test]
    public async Task Crap_JsonFormat_PassingGate_WritesOnlyTheJsonDocument()
    {
        // `dotcov crap --format json > crap.json` must be valid JSON on a pass too: no verdict
        // line may follow the document on stdout.
        var path = _ws.Write("safe.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/S.cs", "MyApp.S", c => c.Method("Safe", "()", "1", m => m.Line(1, hits: 1))));

        var (code, stdout, stderr) = await Run("crap", path, "--format", "json");

        await Assert.That(code).IsEqualTo(0).Because(stderr);
        using var json = JsonDocument.Parse(stdout);
        await Assert.That(json.RootElement.GetProperty("gate").GetProperty("outcome").GetString()).IsEqualTo("pass");
        await Assert.That(json.RootElement.GetProperty("gate").GetProperty("reason").GetString())
            .IsEqualTo("all 1 methods at or below threshold");
    }

    // ── A gate without a verdict blames no files ──

    [Test]
    public async Task Check_BranchNoData_PrintsNoLineOffenderList()
    {
        // A branch threshold on a lines-only report is NODATA, not a line failure, so no
        // offender list follows even though the line rate sits below --min-line.
        var (code, stdout, stderr) = await Run("check", HalfCovered(), "--min-line", "90", "--min-branch", "50");

        await Assert.That(code).IsEqualTo(3);
        await Assert.That(stdout).IsEmpty();
        await Assert.That(stderr).StartsWith("NODATA: line 50.0% (min 90%), branch n/a (min 50%)");
        await Assert.That(stderr).DoesNotContain("files below line threshold");
        await Assert.That(stderr).DoesNotContain("src/A.cs");
    }

    // ── Shared flags on every command that takes them ──

    [Test]
    [Arguments("check")]
    [Arguments("crap")]
    [Arguments("snapshot")]
    public async Task InvalidMaxChars_IsRejectedBeforeAnyReportIsRead(string command)
    {
        var (code, stdout, stderr) = await Run(command, HalfCovered(), "--max-chars", "-1");

        await AssertOneLineError(code, stdout, stderr, "error: Invalid --max-chars value: '-1'");
    }

    [Test]
    public async Task Diff_InvalidMaxChars_IsRejectedBeforeAnyReportIsRead()
    {
        var path = HalfCovered();

        var (code, stdout, stderr) = await Run("diff", path, path, "--max-chars", "1.5");

        await AssertOneLineError(code, stdout, stderr, "error: Invalid --max-chars value: '1.5'");
    }

    [Test]
    [Arguments("markdown")]
    [Arguments("md")]
    public async Task Diff_MarkdownFormats_RenderTheMarkdownDiff(string format)
    {
        var before = HalfCovered("before.cobertura.xml");
        var after = _ws.Write("after.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c.Line(1, hits: 1).Line(2, hits: 1)));

        var (code, stdout, stderr) = await Run("diff", before, after, "--format", format);

        await Assert.That(code).IsEqualTo(0).Because(stderr);
        await Assert.That(stdout).StartsWith("## Coverage Diff 📈");
        await Assert.That(stdout).Contains("**Overall:** 50.0% → 100.0%");
    }

    [Test]
    public async Task Diff_JsonFormat_RendersTheJsonDiff()
    {
        var before = HalfCovered("before.cobertura.xml");
        var after = _ws.Write("after.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c.Line(1, hits: 1).Line(2, hits: 1)));

        var (code, stdout, stderr) = await Run("diff", before, after, "--format", "json");

        await Assert.That(code).IsEqualTo(0).Because(stderr);
        using var json = JsonDocument.Parse(stdout);
        var summary = json.RootElement.GetProperty("summary");
        await Assert.That(summary.GetProperty("before").GetDouble()).IsEqualTo(50);
        await Assert.That(summary.GetProperty("after").GetDouble()).IsEqualTo(100);
        await Assert.That(json.RootElement.GetProperty("files")[0].GetProperty("path").GetString()).IsEqualTo("src/A.cs");
    }

    [Test]
    public async Task Report_Threshold_IsRenderedInTheMarkdownVerdict()
    {
        var (passCode, passOut, _) = await Run("report", HalfCovered(), "--format", "md", "--threshold", "40");
        var (failCode, failOut, _) = await Run("report", HalfCovered(), "--format", "md", "--threshold", "60");

        // Presentation only: the exit code ignores --threshold.
        await Assert.That(passCode).IsEqualTo(0);
        await Assert.That(failCode).IsEqualTo(0);
        await Assert.That(passOut).StartsWith("## Coverage Report ✅");
        await Assert.That(passOut).Contains("**Threshold:** 40%");
        await Assert.That(failOut).StartsWith("## Coverage Report ❌");
        await Assert.That(failOut).Contains("**Threshold:** 60%");
    }

    [Test]
    public async Task Snapshot_EmptyDirectory_HasNoFileHash()
    {
        // Nothing was resolved, so there is nothing to hash; the key is omitted, not invented.
        var empty = _ws.CreateDirectory("empty");

        var (code, stdout, stderr) = await Run(
            "snapshot", empty, "--commit", "abc123", "--branch", "main", "--project", "app");

        await Assert.That(code).IsEqualTo(0).Because(stderr);
        using var json = JsonDocument.Parse(stdout);
        await Assert.That(json.RootElement.TryGetProperty("fileHash", out _)).IsFalse();
        await Assert.That(json.RootElement.GetProperty("files").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task Snapshot_SingleFile_HashesExactlyThatFile()
    {
        var path = HalfCovered();

        var (code, stdout, _) = await Run(
            "snapshot", path, "--commit", "abc123", "--branch", "main", "--project", "app");

        await Assert.That(code).IsEqualTo(0);
        using var json = JsonDocument.Parse(stdout);
        await Assert.That(json.RootElement.GetProperty("fileHash").GetString()).IsEqualTo(FileHasher.ComputeHash(path));
    }

    /// <summary>Program.cs uncovered (excluded by the well-known rules), src/A.cs fully covered.</summary>
    private string WithProgramCs() => _ws.Write("program.cobertura.xml", Cobertura.NewDoc()
        .AddClass("src/Program.cs", "Program", c => c.Method("Main", "()", "9", m => m.Line(1, hits: 0)))
        .AddClass("src/A.cs", "MyApp.A", c => c.Method("M", "()", "1", m => m.Line(1, hits: 1))));

    [Test]
    public async Task Report_KeepExemptsAFileFromExcludeGenerated()
    {
        var path = WithProgramCs();

        var (_, excluded, _) = await Run("report", path, "--exclude-generated", "--format", "json");
        var (code, kept, stderr) = await Run(
            "report", path, "--exclude-generated", "--keep", " Other.cs , program.cs ", "--format", "json");

        await Assert.That(code).IsEqualTo(0).Because(stderr);
        using var excludedJson = JsonDocument.Parse(excluded);
        using var keptJson = JsonDocument.Parse(kept);
        await Assert.That(excludedJson.RootElement.GetProperty("summary").GetProperty("lineRate").GetDouble()).IsEqualTo(100);
        await Assert.That(keptJson.RootElement.GetProperty("summary").GetProperty("lineRate").GetDouble()).IsEqualTo(50);
        await Assert.That(kept).Contains("src/Program.cs");
    }

    [Test]
    public async Task Crap_KeepExemptsAMethodFileFromExcludeGenerated()
    {
        var path = WithProgramCs();

        var (excludedCode, _, _) = await Run("crap", path, "--exclude-generated");
        var (code, stdout, stderr) = await Run("crap", path, "--exclude-generated", "--keep", "Program.cs");

        await Assert.That(excludedCode).IsEqualTo(0);
        await Assert.That(code).IsEqualTo(1);
        await Assert.That(stderr).StartsWith("FAIL: worst CRAP 90.0 (max 30)");
        await Assert.That(stdout).Contains("Program.Main");
    }

    // ── Warnings keep whatever location they carry ──

    [Test]
    public async Task Report_DocumentScopedWarning_IsWrittenWithoutALocation()
    {
        // Two <source> roots: the warning belongs to the document, not to a file or line.
        var path = _ws.Write("roots.cobertura.xml", Cobertura.NewDoc()
            .WithSource("/a").WithSource("/b")
            .AddClass("x/Foo.cs", c => c.Line(1, hits: 1)));

        var (code, _, stderr) = await Run("report", path);

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stderr).StartsWith("warning: multiple <source> roots - resolving relative filenames against the first ('/a')");
    }

    [Test]
    public async Task Report_FileScopedWarning_NamesTheFileWithoutALine()
    {
        // Same file name under different source roots: a merge-time warning about a whole file.
        _ws.Write("roots/a/coverage.cobertura.xml", Cobertura.NewDoc()
            .WithSource("/a").AddClass("x/Foo.cs", c => c.Line(1, hits: 1)));
        _ws.Write("roots/b/coverage.cobertura.xml", Cobertura.NewDoc()
            .WithSource("/b").AddClass("y/Foo.cs", c => c.Line(1, hits: 0)));

        var (code, _, stderr) = await Run("report", _ws.PathOf("roots"));

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stderr).StartsWith("warning: /a/x/Foo.cs: '/a/x/Foo.cs' and '/b/y/Foo.cs' share a file name");
    }
}
