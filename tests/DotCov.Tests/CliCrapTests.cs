using DotCov.Tests.Infrastructure;
using DotCov.Tool;
using Xunit;

namespace DotCov.Tests;

/// <summary>
/// Pins the <c>dotcov crap</c> CLI contract: exit codes (0 pass, 1 fail/NODATA/bad flags —
/// the same fail-closed policy as <c>check</c>), the at-threshold-passes default gate,
/// --metrics/--top/--format handling, and the stderr first-token discriminator.
/// </summary>
public sealed class CliCrapTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("dotcov-cli-crap-");

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

    /// <summary>comp 2, cov 0 → CRAP exactly 6: passes the default gate only because at-threshold passes.</summary>
    private string AtDefaultThreshold() =>
        WriteFile("at.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/A.cs", "MyApp.A", c => c.Method("M", "()", "2", m => m.Line(1, hits: 0)))
            .ToBytes());

    /// <summary>comp 3, cov 0 → CRAP 12: fails the default gate.</summary>
    private string AboveDefaultThreshold() =>
        WriteFile("above.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/A.cs", "MyApp.A", c => c
                .Method("Risky", "()", "3", m => m.Line(1, hits: 0))
                .Method("Safe", "()", "1", m => m.Line(5, hits: 1)))
            .ToBytes());

    /// <summary>Method detail present but no usable complexity anywhere.</summary>
    private string NoComplexity() =>
        WriteFile("nocomp.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/B.cs", "MyApp.B", c => c.Method("M", "()", null, m => m.Line(1, hits: 1)))
            .ToBytes());

    // ── Exit-code matrix ──

    [Fact]
    public async Task Crap_AtThresholdExactly_Exits0()
    {
        var (code, stdout, _) = await Run("crap", AtDefaultThreshold());

        Assert.Equal(0, code);
        Assert.Contains("PASS: worst CRAP 6.0 (max 6)", stdout);
    }

    [Fact]
    public async Task Crap_AboveThreshold_Exits1_VerdictOnStderr()
    {
        var (code, stdout, stderr) = await Run("crap", AboveDefaultThreshold());

        Assert.Equal(1, code);
        Assert.StartsWith("FAIL:", stderr);
        Assert.Contains("1 of 2 methods above threshold", stderr);
        Assert.Contains("MyApp.A.Risky", stdout);   // the worst-first table still lands on stdout
    }

    [Fact]
    public async Task Crap_RaisedThreshold_TurnsSameReportGreen()
    {
        var (code, _, _) = await Run("crap", AboveDefaultThreshold(), "--max-crap", "12");

        Assert.Equal(0, code);   // 12 is at-threshold for the Risky method → passes
    }

    [Fact]
    public async Task Crap_NoMethodDetail_Nodata_Exits1()
    {
        var noMethods = WriteFile("plain.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/C.cs", c => c.Line(1, hits: 1))
            .ToBytes());

        var (code, _, stderr) = await Run("crap", noMethods);

        Assert.Equal(1, code);
        Assert.StartsWith("NODATA:", stderr);
    }

    [Fact]
    public async Task Crap_NoComplexitySource_Nodata_MentionsMetricsFlag()
    {
        var (code, _, stderr) = await Run("crap", NoComplexity());

        Assert.Equal(1, code);
        Assert.StartsWith("NODATA:", stderr);
        Assert.Contains("--metrics", stderr);
    }

    [Fact]
    public async Task Crap_DirectoryInput_AggregatesLikeReport()
    {
        // The same directory dispatch as report/check: point crap at a TestResults-style
        // directory and the default **/coverage.cobertura.xml pattern finds nested reports.
        WriteFile("run-1/coverage.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/A.cs", "MyApp.A", c => c.Method("M", "()", "2", m => m.Line(1, hits: 0)))
            .ToBytes());

        var (code, stdout, _) = await Run("crap", _dir.FullName);

        Assert.Equal(0, code);
        Assert.Contains("MyApp.A.M", stdout);
    }

    [Fact]
    public async Task Crap_DirectoryWithUnsupportedPattern_Error_Exits1()
    {
        // The pattern gate rejection surfaces as a CliError, not a raw ArgumentException.
        AtDefaultThreshold();

        var (code, _, stderr) = await Run("crap", _dir.FullName, "--pattern", "sub/dir/coverage.xml");

        Assert.Equal(1, code);
        Assert.StartsWith("error:", stderr);
        Assert.Contains("Unsupported pattern", stderr);
    }

    [Fact]
    public async Task Crap_MissingPath_Error_Exits1()
    {
        var (code, _, stderr) = await Run("crap", Path.Combine(_dir.FullName, "nope.xml"));

        Assert.Equal(1, code);
        Assert.StartsWith("error:", stderr);
    }

    [Fact]
    public async Task Crap_NoArgs_Usage_Exits1()
    {
        var (code, _, stderr) = await Run("crap");

        Assert.Equal(1, code);
        Assert.Contains("Usage: dotcov crap", stderr);
    }

    [Theory]
    [InlineData("--max-crap", "abc")]
    [InlineData("--top", "0")]
    [InlineData("--top", "-3")]
    [InlineData("--format", "yaml")]
    public async Task Crap_InvalidFlagValue_Exits1(string flag, string value)
    {
        var (code, _, stderr) = await Run("crap", AtDefaultThreshold(), flag, value);

        Assert.Equal(1, code);
        Assert.Contains("Invalid", stderr);
    }

    [Fact]
    public async Task Crap_MissingMetricsFile_Error_Exits1()
    {
        var (code, _, stderr) = await Run("crap", AtDefaultThreshold(), "--metrics", "/nonexistent/metrics.xml");

        Assert.Equal(1, code);
        Assert.StartsWith("error: No metrics file at", stderr);
    }

    // ── --metrics path ──

    [Fact]
    public async Task Crap_MetricsFile_SuppliesComplexity_GatesOnIt()
    {
        // Coverage carries no complexity; the metrics file supplies comp 5 for the uncovered
        // method M → CRAP 30 → fail. The zero-extra-file path is preferred only when usable.
        var coverage = WriteFile("mixed.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/B.cs", "MyApp.B", c => c.Method("M", "(System.Int32)", null, m => m.Line(1, hits: 0)))
            .ToBytes());
        var metricsPath = Path.Combine(_dir.FullName, "metrics.xml");
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
                                  <Metric Name="CyclomaticComplexity" Value="5" />
                                </Metrics>
                              </Method>
                              <Method Name="void B.NeverCovered()">
                                <Metrics>
                                  <Metric Name="CyclomaticComplexity" Value="2" />
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

        Assert.Equal(1, code);
        Assert.Contains("FAIL: worst CRAP 30.0", stderr);
        // The unmatched metrics member is listed, never silently dropped.
        Assert.Contains("void B.NeverCovered()", stdout);
    }

    // ── Output formats ──

    [Fact]
    public async Task Crap_JsonFormat_EmitsGateAndMethods()
    {
        var (code, stdout, _) = await Run("crap", AboveDefaultThreshold(), "--format", "json");

        Assert.Equal(1, code);
        var root = System.Text.Json.JsonDocument.Parse(
            stdout[..(stdout.LastIndexOf('}') + 1)]).RootElement;
        Assert.Equal("fail", root.GetProperty("gate").GetProperty("outcome").GetString());
        Assert.Equal("MyApp.A.Risky", root.GetProperty("methods")[0].GetProperty("method").GetString());
    }

    [Fact]
    public async Task Crap_MarkdownFormat_EmitsBadgeAndVerdict()
    {
        var (code, stdout, _) = await Run("crap", AtDefaultThreshold(), "--format", "md");

        Assert.Equal(0, code);
        Assert.Contains("## CRAP Report ✅", stdout);
        Assert.Contains("`PASS:", stdout);
    }

    [Fact]
    public async Task Crap_Top_TruncatesTable()
    {
        var (_, stdout, _) = await Run("crap", AboveDefaultThreshold(), "--top", "1");

        Assert.Contains("MyApp.A.Risky", stdout);
        Assert.DoesNotContain("MyApp.A.Safe", stdout);
        Assert.Contains("1 more methods below", stdout);
    }

    [Fact]
    public async Task Crap_ExcludeGenerated_DropsExcludedFilesFromGate()
    {
        // A high-CRAP method in Program.cs (excluded by the WellKnown rules) must not fail the
        // gate once --exclude-generated is on — same rule set as report/check.
        var mixed = WriteFile("gen.cobertura.xml", Cobertura.NewDoc()
            .AddClass("Program.cs", "Program", c => c.Method("Main", "()", "9", m => m.Line(1, hits: 0)))
            .AddClass("src/A.cs", "MyApp.A", c => c.Method("M", "()", "1", m => m.Line(1, hits: 1)))
            .ToBytes());

        var (withoutFlag, _, _) = await Run("crap", mixed);
        var (withFlag, stdout, _) = await Run("crap", mixed, "--exclude-generated");

        Assert.Equal(1, withoutFlag);
        Assert.Equal(0, withFlag);
        Assert.DoesNotContain("Program", stdout);
    }
}
