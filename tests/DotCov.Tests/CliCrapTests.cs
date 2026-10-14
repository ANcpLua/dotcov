using DotCov.Tests.Infrastructure;
using DotCov.Tool;

namespace DotCov.Tests;

/// <summary>
/// Pins the <c>dotcov crap</c> CLI contract: exit codes (0 pass, 1 fail/NODATA/bad flags —
/// the same fail-closed policy as <c>check</c>), the at-threshold-passes default gate,
/// --metrics/--top/--format handling, and the stderr first-token discriminator.
/// </summary>
public sealed class CliCrapTests : IDisposable
{
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-cli-crap-");

    public void Dispose() => _ws.Dispose();

    private static async Task<(int Code, string StdOut, string StdErr)> Run(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await DotCovCli.RunAsync(args, stdout, stderr);
        return (code, stdout.ToString(), stderr.ToString());
    }

    /// <summary>comp 5, cov 0 → CRAP exactly 30: passes the default gate only because at-threshold passes.</summary>
    private string AtDefaultThreshold() =>
        _ws.Write("at.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/A.cs", "MyApp.A", c => c.Method("M", "()", "5", m => m.Line(1, hits: 0)))
            .ToBytes());

    /// <summary>comp 6, cov 0 → CRAP 42: fails the default gate.</summary>
    private string AboveDefaultThreshold() =>
        _ws.Write("above.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/A.cs", "MyApp.A", c => c
                .Method("Risky", "()", "6", m => m.Line(1, hits: 0))
                .Method("Safe", "()", "1", m => m.Line(5, hits: 1)))
            .ToBytes());

    /// <summary>Method detail present but no usable complexity anywhere.</summary>
    private string NoComplexity() =>
        _ws.Write("nocomp.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/B.cs", "MyApp.B", c => c.Method("M", "()", null, m => m.Line(1, hits: 1)))
            .ToBytes());

    // ── Exit-code matrix ──

    [Test]
    public async Task Crap_AtThresholdExactly_Exits0()
    {
        var (code, stdout, stderr) = await Run("crap", AtDefaultThreshold());

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stderr).StartsWith("PASS: worst CRAP 30.0 (max 30)");
        await Assert.That(stdout).DoesNotContain("PASS:");
    }

    [Test]
    public async Task Crap_AboveThreshold_Exits1_VerdictOnStderr()
    {
        var (code, stdout, stderr) = await Run("crap", AboveDefaultThreshold());

        await Assert.That(code).IsEqualTo(1);
        await Assert.That(stderr).StartsWith("FAIL:");
        await Assert.That(stderr).Contains("1 of 2 methods above threshold");
        await Assert.That(stdout).Contains("MyApp.A.Risky");   // the worst-first table still lands on stdout
    }

    [Test]
    public async Task Crap_RaisedThreshold_TurnsSameReportGreen()
    {
        var (code, _, _) = await Run("crap", AboveDefaultThreshold(), "--max-crap", "42");

        await Assert.That(code).IsEqualTo(0);   // 42 is at-threshold for the Risky method → passes
    }

    [Test]
    public async Task Crap_InfiniteThreshold_IsDisabled_Exits4()
    {
        // Nothing exceeds an infinite threshold, so the gate cannot fail and must not report a pass.
        var (code, _, stderr) = await Run("crap", AboveDefaultThreshold(), "--max-crap", "Infinity");

        await Assert.That(code).IsEqualTo(4);
        await Assert.That(stderr).StartsWith("DISABLED:");
    }

    [Test]
    public async Task Crap_NoMethodDetail_Nodata_Exits3()
    {
        var noMethods = _ws.Write("plain.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/C.cs", c => c.Line(1, hits: 1))
            .ToBytes());

        var (code, _, stderr) = await Run("crap", noMethods);

        await Assert.That(code).IsEqualTo(3);
        await Assert.That(stderr).StartsWith("NODATA:");
    }

    [Test]
    public async Task Crap_NoComplexitySource_Nodata_MentionsMetricsFlag()
    {
        var (code, _, stderr) = await Run("crap", NoComplexity());

        await Assert.That(code).IsEqualTo(3);
        await Assert.That(stderr).StartsWith("NODATA:");
        await Assert.That(stderr).Contains("--metrics");
    }

    [Test]
    public async Task Crap_DirectoryInput_AggregatesLikeReport()
    {
        // The same directory dispatch as report/check: point crap at a TestResults-style
        // directory and the default recursive Cobertura pattern finds nested reports.
        _ws.Write("run-1/coverage.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/A.cs", "MyApp.A", c => c.Method("M", "()", "2", m => m.Line(1, hits: 0)))
            .ToBytes());

        var (code, stdout, _) = await Run("crap", _ws.Root);

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stdout).Contains("MyApp.A.M");
    }

    [Test]
    public async Task Crap_ParserWarnings_AreWrittenToStderr_AndDoNotChangeTheVerdict()
    {
        // A malformed hits attribute inside a method degrades to 0 hits; the CRAP path must
        // say so — every warning, one line each — while the gate still decides on the data.
        var path = _ws.Write("warn.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/A.cs", "MyApp.A", c => c.Method("M", "()", "1", m => m.MalformedLine("1", "lots").Line(2, hits: 1)))
            .ToBytes());

        var (code, _, stderr) = await Run("crap", path);

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stderr).StartsWith("PASS:");
        await Assert.That(stderr).Contains("warning: src/A.cs:1: hits='lots' could not be parsed");
        await Assert.That(stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(l => l.StartsWith("warning:"))).IsEqualTo(1);
    }

    [Test]
    public async Task Crap_EmptyDirectory_Nodata_Exits3()
    {
        var dir = _ws.CreateDirectory("empty");

        var (code, _, stderr) = await Run("crap", dir);

        await Assert.That(code).IsEqualTo(3);
        await Assert.That(stderr).StartsWith("NODATA:");
    }

    [Test]
    public async Task Crap_MalformedReport_ErrorNamesTheFileOnce()
    {
        var bad = _ws.Write("bad/coverage.cobertura.xml", "<coverage><packa"u8.ToArray());

        var (code, _, stderr) = await Run("crap", _ws.PathOf("bad"));

        await Assert.That(code).IsEqualTo(5);
        await Assert.That(stderr).StartsWith($"error: {bad}: ");
        await Assert.That(stderr).DoesNotContain($"{bad}: {bad}");
    }

    [Test]
    public async Task Crap_DirectoryWithUnsupportedPattern_Error_Exits5()
    {
        // The pattern gate rejection surfaces as a CliError, not a raw ArgumentException.
        AtDefaultThreshold();

        var (code, _, stderr) = await Run("crap", _ws.Root, "--pattern", "sub/dir/coverage.xml");

        await Assert.That(code).IsEqualTo(2);
        await Assert.That(stderr).StartsWith("error:");
        await Assert.That(stderr).Contains("Unsupported pattern");
    }

    [Test]
    public async Task Crap_MissingPath_Error_Exits5()
    {
        var (code, _, stderr) = await Run("crap", _ws.PathOf("nope.xml"));

        await Assert.That(code).IsEqualTo(5);
        await Assert.That(stderr).StartsWith("error:");
    }

    [Test]
    public async Task Crap_NoArgs_Usage_Exits2()
    {
        var (code, _, stderr) = await Run("crap");

        await Assert.That(code).IsEqualTo(2);
        await Assert.That(stderr).StartsWith("error: missing path. Usage: dotcov crap");
    }

    [Test]
    [Arguments("--max-crap", "abc")]
    [Arguments("--top", "0")]
    [Arguments("--top", "-3")]
    [Arguments("--format", "yaml")]
    public async Task Crap_InvalidFlagValue_Exits2(string flag, string value)
    {
        var (code, _, stderr) = await Run("crap", AtDefaultThreshold(), flag, value);

        await Assert.That(code).IsEqualTo(2);
        await Assert.That(stderr).Contains("Invalid");
    }

    [Test]
    public async Task Crap_MissingMetricsFile_Error_Exits5()
    {
        var (code, _, stderr) = await Run("crap", AtDefaultThreshold(), "--metrics", "/nonexistent/metrics.xml");

        await Assert.That(code).IsEqualTo(5);
        await Assert.That(stderr).StartsWith("error: No metrics file at");
    }

    // ── --metrics path ──

    [Test]
    public async Task Crap_MetricsFile_SuppliesComplexity_GatesOnIt()
    {
        // Coverage carries no complexity; the metrics file supplies comp 6 for the uncovered
        // method M → CRAP 42 → fail. The zero-extra-file path is preferred only when usable.
        var coverage = _ws.Write("mixed.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/B.cs", "MyApp.B", c => c.Method("M", "(System.Int32)", null, m => m.Line(1, hits: 0)))
            .ToBytes());
        var metricsPath = _ws.PathOf("metrics.xml");
        await File.WriteAllTextAsync(metricsPath, """
            <?xml version="1.0" encoding="utf-8"?>
            <CodeMetricsReport Version="1.0">
              <Targets>
                <Target Name="MyApp.csproj">
                  <Assembly Name="MyApp">
                    <Namespaces>
                      <Namespace Name="MyApp">
                        <Types>
                          <NamedType Name="B">
                            <Members>
                              <Method Name="void B.M(int value)">
                                <Metrics>
                                  <Metric Name="CyclomaticComplexity" Value="6" />
                                </Metrics>
                              </Method>
                            </Members>
                          </NamedType>
                        </Types>
                      </Namespace>
                    </Namespaces>
                  </Assembly>
                </Target>
              </Targets>
            </CodeMetricsReport>
            """);

        var (code, stdout, stderr) = await Run("crap", coverage, "--metrics", metricsPath);

        await Assert.That(code).IsEqualTo(1);
        await Assert.That(stderr).Contains("FAIL: worst CRAP 42.0");
        await Assert.That(stdout).Contains("MyApp.B.M");
    }

    // ── Output formats ──

    [Test]
    [Arguments("table")]
    [Arguments("md")]
    [Arguments("json")]
    public async Task Crap_UninstrumentedMetricsMethod_WithholdsPass_Exits3(string format)
    {
        var metrics = _ws.Write("incomplete.Metrics.xml", CodeMetricsReportBuilder.Create()
            .Namespace("MyApp", t => t.Type("A", m => m
                .Method("void A.M()", 5)
                .Method("void A.NeverCovered()", 9)))
            .ToBytes());

        var (code, stdout, stderr) = await Run("crap", AtDefaultThreshold(), "--metrics", metrics, "--format", format);

        await Assert.That(code).IsEqualTo(DotCovCli.NoDataExitCode);
        await Assert.That(stderr).StartsWith("NODATA: worst CRAP 30.0 (max 30)");
        await Assert.That(stderr).Contains("1 metrics members without coverage data");
        await Assert.That(stdout).DoesNotContain("PASS:");
        await Assert.That(stdout).Contains("void A.NeverCovered()");
        if (format is "json")
        {
            using var json = System.Text.Json.JsonDocument.Parse(stdout);
            await Assert.That(json.RootElement.GetProperty("gate").GetProperty("outcome").GetString()).IsEqualTo("nodata");
        }
    }

    [Test]
    public async Task Crap_JsonFormat_EmitsGateAndMethods()
    {
        var (code, stdout, _) = await Run("crap", AboveDefaultThreshold(), "--format", "json");

        await Assert.That(code).IsEqualTo(1);
        var root = System.Text.Json.JsonDocument.Parse(
            stdout[..(stdout.LastIndexOf('}') + 1)]).RootElement;
        await Assert.That(root.GetProperty("gate").GetProperty("outcome").GetString()).IsEqualTo("fail");
        await Assert.That(root.GetProperty("methods")[0].GetProperty("method").GetString()).IsEqualTo("MyApp.A.Risky");
    }

    [Test]
    public async Task Crap_Table_LocatesEachMethodByFileAndStartLine()
    {
        var (_, stdout, _) = await Run("crap", AboveDefaultThreshold());

        var rows = stdout.Split(Environment.NewLine);
        await Assert.That(rows[0]).EndsWith("CRAP  Location");
        await Assert.That(rows.Single(static r => r.StartsWith("MyApp.A.Risky", StringComparison.Ordinal))).EndsWith("  src/A.cs:1");
        await Assert.That(rows.Single(static r => r.StartsWith("MyApp.A.Safe", StringComparison.Ordinal))).EndsWith("  src/A.cs:5");
    }

    [Test]
    public async Task Crap_Markdown_LocatesEachMethodByFileAndStartLine()
    {
        var (_, stdout, _) = await Run("crap", AboveDefaultThreshold(), "--format", "md");

        await Assert.That(stdout).Contains("| Method | Comp | Cov % | CRAP | Location |");
        await Assert.That(stdout).Contains("| `MyApp.A.Risky` | 6 | 0.0% | 42.0 ❌ | `src/A.cs:1` |");
    }

    [Test]
    public async Task Crap_Json_GivesEachMethodItsLocationAndUncoveredRanges()
    {
        var report = _ws.Write("ranges.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/A.cs", "MyApp.A", c => c
                .Method("Partly", "()", "2", m => m.Line(10, hits: 1).Line(11, hits: 0).Line(12, hits: 0).Line(14, hits: 0))
                .Method("Fully", "()", "1", m => m.Line(20, hits: 1))));

        var (code, stdout, stderr) = await Run("crap", report, "--format", "json");

        await Assert.That(code).IsEqualTo(0).Because(stderr);
        var root = System.Text.Json.JsonDocument.Parse(stdout).RootElement;
        var gate = root.GetProperty("gate");
        await Assert.That(gate.GetProperty("exitCode").GetInt32()).IsEqualTo(0);
        await Assert.That(gate.GetProperty("reason").GetString()).IsEqualTo("PASS: worst CRAP 3.7 (max 30) - all 2 methods at or below threshold");
        var methods = root.GetProperty("methods").EnumerateArray().ToDictionary(static m => m.GetProperty("method").GetString()!);
        var partly = methods["MyApp.A.Partly"];
        await Assert.That(partly.GetProperty("file").GetString()).IsEqualTo("src/A.cs");
        await Assert.That(partly.GetProperty("line").GetInt32()).IsEqualTo(10);
        await Assert.That(partly.GetProperty("uncovered").GetString()).IsEqualTo("11-12,14");
        await Assert.That(methods["MyApp.A.Fully"].TryGetProperty("uncovered", out _)).IsFalse();
    }

    [Test]
    [Arguments("--max-crap", "6", 1, "fail")]
    [Arguments("--max-crap", "Infinity", 4, "disabled")]
    public async Task Crap_Json_GateCarriesTheExitCodeAndTheVerdictLine(string flag, string value, int exitCode, string outcome)
    {
        var (code, stdout, stderr) = await Run("crap", AboveDefaultThreshold(), "--format", "json", flag, value);

        await Assert.That(code).IsEqualTo(exitCode);
        var gate = System.Text.Json.JsonDocument.Parse(stdout).RootElement.GetProperty("gate");
        await Assert.That(gate.GetProperty("outcome").GetString()).IsEqualTo(outcome);
        await Assert.That(gate.GetProperty("exitCode").GetInt32()).IsEqualTo(exitCode);
        await Assert.That(gate.GetProperty("reason").GetString()).IsEqualTo(stderr.Split(Environment.NewLine)[0]);
    }

    [Test]
    public async Task Crap_MarkdownFormat_EmitsBadgeAndVerdict()
    {
        var (code, stdout, _) = await Run("crap", AtDefaultThreshold(), "--format", "md");

        await Assert.That(code).IsEqualTo(0);
        await Assert.That(stdout).Contains("## CRAP Report ✅");
        await Assert.That(stdout).Contains("`PASS:");
    }

    [Test]
    public async Task Crap_Top_TruncatesTable()
    {
        var (_, stdout, _) = await Run("crap", AboveDefaultThreshold(), "--top", "1");

        await Assert.That(stdout).Contains("MyApp.A.Risky");
        await Assert.That(stdout).DoesNotContain("MyApp.A.Safe");
        await Assert.That(stdout).Contains("1 more methods below");
    }

    [Test]
    public async Task Crap_ExcludeGenerated_DropsExcludedFilesFromGate()
    {
        // A high-CRAP method in Program.cs (excluded by the WellKnown rules) must not fail the
        // gate once --exclude-generated is on — same rule set as report/check.
        var mixed = _ws.Write("gen.cobertura.xml", Cobertura.NewDoc()
            .AddClass("Program.cs", "Program", c => c.Method("Main", "()", "9", m => m.Line(1, hits: 0)))
            .AddClass("src/A.cs", "MyApp.A", c => c.Method("M", "()", "1", m => m.Line(1, hits: 1)))
            .ToBytes());

        var (withoutFlag, _, _) = await Run("crap", mixed);
        var (withFlag, stdout, _) = await Run("crap", mixed, "--exclude-generated");

        await Assert.That(withoutFlag).IsEqualTo(1);
        await Assert.That(withFlag).IsEqualTo(0);
        await Assert.That(stdout).DoesNotContain("Program");
    }

    // ── Complete scoring: one unscored method withholds the verdict ──

    /// <summary>A scored method well below the gate beside one the report gives no complexity for.</summary>
    private string PartlyScored() =>
        _ws.Write("partly.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/A.cs", "MyApp.A", c => c
                .Method("Scored", "()", "1", m => m.Line(1, hits: 1))
                .Method("Unscored", "()", null, m => m.Line(5, hits: 0)))
            .ToBytes());

    [Test]
    [Arguments("table")]
    [Arguments("md")]
    [Arguments("json")]
    public async Task Crap_OneUnscoredMethod_IsNoData_Exits3_AndStillListsIt(string format)
    {
        // 2.x passed this report: the only scored method sits far below 30, and the method it could
        // not score was listed and ignored. That method may be the one above the limit.
        var (code, stdout, stderr) = await Run("crap", PartlyScored(), "--format", format);

        await Assert.That(code).IsEqualTo(DotCovCli.NoDataExitCode);
        await Assert.That(stderr).StartsWith("NODATA: worst CRAP 1.0 (max 30) - 1 of 2 methods could not be scored - supply --metrics <file>");
        await Assert.That(stdout).DoesNotContain("PASS:");
        await Assert.That(stdout).Contains("MyApp.A.Unscored");
        await Assert.That(stdout).Contains(format is "json" ? "\"outcome\": \"nodata\"" : "no complexity in the coverage report");
    }

    [Test]
    public async Task Crap_MetricsFileScoringTheRest_TurnsTheSameReportIntoAMeasuredPass()
    {
        // The remedy the reason names: --metrics supplies the missing complexity.
        var metrics = _ws.Write("A.Metrics.xml", CodeMetricsReportBuilder.Create()
            .Namespace("MyApp", t => t.Type("A", m => m.Method("void A.Unscored()", 1)))
            .ToBytes());

        var (code, _, stderr) = await Run("crap", PartlyScored(), "--metrics", metrics);

        await Assert.That(code).IsEqualTo(0).Because(stderr);
        await Assert.That(stderr).StartsWith("PASS: worst CRAP 2.0 (max 30) - all 2 methods at or below threshold");
    }

    // Istanbul (Vitest, c8, Jest) and coverage.py write no complexity: --metrics, a .NET tool, cannot help.
    [Test]
    public async Task Crap_ReportWithoutAnyComplexity_SaysWhichEmittersWriteNone()
    {
        using var ws = TempWorkspace.Create("dotcov-crap-istanbul-");
        var report = ws.Write("cobertura-coverage.xml", Cobertura.NewDoc().AddClass("src/shipping.ts", "shipping.ts", c => c
            .Method("shippingCost", "()", null, m => m.Line(1, 1))
            .Method("label", "()", null, m => m.Line(11, 0))).ToBytes());
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var code = await DotCovCli.RunAsync(["crap", report], stdout, stderr);

        await Assert.That(code).IsEqualTo(3);
        await Assert.That(stderr.ToString()).StartsWith(
            "NODATA: worst CRAP n/a (max 30) - the report carries no complexity - Istanbul (Vitest, c8, Jest) and coverage.py write none");
        await Assert.That(stderr.ToString()).DoesNotContain("dotnet msbuild");
    }
}

