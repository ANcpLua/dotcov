using System.Text;
using System.Text.Json;
using DotCov.Tests.Infrastructure;
using DotCov.Tool;

namespace DotCov.Tests;

public sealed class CrapMetricsScopeTests
{
    [Test]
    [Arguments("0", 0)]
    [Arguments("3", 3)]
    [Arguments("-1", null)]
    [Arguments("unknown", null)]
    [Arguments("", null)]
    [Arguments("2147483648", null)]
    public async Task ExecutableLines_ArePreserved_AndOnlyExplicitZeroExemptsMissingCoverage(
        string value, int? expected)
    {
        var xml = $"""
            <CodeMetricsReport><Namespace Name="App"><NamedType Name="A"><Members>
              <Method Name="void A.Missing()"><Metrics>
                <Metric Name="ExecutableLines" Value="{value}" />
                <Metric Name="CyclomaticComplexity" Value="1" />
              </Metrics></Method>
            </Members></NamedType></Namespace></CodeMetricsReport>
            """;
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var metrics = CodeMetricsReader.Parse(input);
        await Assert.That(metrics.Single().ExecutableLines).IsEqualTo(expected);

        var report = CrapAnalysis.Analyze(KeptCoverage(), metrics);
        await Assert.That(report.Evaluate(30).Outcome)
            .IsEqualTo(expected is 0 ? GateOutcome.Pass : GateOutcome.NoData);
    }

    [Test]
    public async Task ExecutableLines_StayOnTheirOwnMember_AndDoNotEraseMeasuredBodies()
    {
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m
            .Method("void A.Kept()", 6, 0)
            .Method("void A.InterfaceMethod()", 1, 0)
            .Property("int A.AbstractValue", 2, a => a
                .Method("int A.AbstractValue.get", 1, 0)
                .Method("void A.AbstractValue.set", 1, 0))
            .Method("void A.Unknown()", 1));

        await Assert.That(metrics.Single(m => m.MemberName == "AbstractValue").ExecutableLines).IsNull();
        await Assert.That(metrics.Single(m => m.MemberName == "Unknown").ExecutableLines).IsNull();
        var report = CrapAnalysis.Analyze(KeptCoverage(), metrics);
        await Assert.That(report.UnmatchedMetricsMembers).IsEquivalentTo(["void A.Unknown()"]);
        await Assert.That(report.Methods.Single().Method).IsEqualTo("App.A.Kept");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RepeatedExclusions_AccountForRemovedIdentities_WithoutScoringThem(bool withMetrics)
    {
        var coverage = Cobertura.NewDoc()
            .AddClass("src/A.cs", "App.A", c => c.Method("Kept", "()", "1", m => m.Line(1, 1)))
            .AddClass("Program.cs", "App.Program", c => c.Method("Main", "()", null, _ => { }))
            .AddClass("tests/Test.cs", "App.Test", c => c.Method("Run", "()", "9", m => m.Line(1, 0)))
            .AddClass("tests/Test.cs", "App.Test/<>c", c => c.Method("<Run>b__0_0", "()", "9", m => m.Line(2, 0)));
        using var input = new MemoryStream(coverage.ToBytes());
        var original = CoberturaParser.ParseMethods(input);
        var filtered = original.Exclude(["Program.cs"], []).Exclude(["tests/"], []);
        var metrics = CodeMetricsReportBuilder.Create().Namespace("App", t => t
            .Type("A", m => m.Method("void A.Kept()", 1))
            .Type("Program", m => m.Method("void Program.Main()", 9))
            .Type("Test", m => m.Method("void Test.Run()", 9))).Parse();

        var report = CrapAnalysis.Analyze(filtered, withMetrics ? metrics : null);
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.Pass);
        await Assert.That(report.Methods.Count).IsEqualTo(1);
        await Assert.That(report.Unscored).IsEmpty();
        await Assert.That(report.UnmatchedMetricsMembers).IsEmpty();
        await Assert.That(original.Methods.Count).IsEqualTo(4);
        await Assert.That(original.Exclusion.ExcludedFiles).IsEmpty();
    }

    [Test]
    [Arguments(false, false, 0, "pass")]
    [Arguments(true, false, 1, "fail")]
    [Arguments(false, true, 3, "nodata")]
    public async Task Cli_ExclusionsAndDeclarations_RespectSelectedScope(
        bool keepProgram, bool missingExecutable, int expectedExit, string expectedOutcome)
    {
        using var ws = TempWorkspace.Create("dotcov-crap-scope-");
        var coverage = ws.Write("coverage.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/A.cs", "App.A", c => c.Method("Kept", "()", null, m => m.Line(1, 1)))
            .AddClass("Program.cs", "App.Program/<Main>d__0", c => c.Method("MoveNext", "()", "9", m => m.Line(1, 0)))
            .ToBytes());
        var metrics = ws.Write("metrics.xml", CodeMetricsReportBuilder.Create().Namespace("App", t => t
            .Type("A", m => m.Method("void A.Kept()", 1, 1)
                .Method("void A.Missing()", 1, missingExecutable ? 1 : 0)
                .Property("int A.AbstractValue", 1, a => a.Method("int A.AbstractValue.get", 1, 0)))
            .Type("Program", m => m.Method("void Program.Main()", 9, 1))).ToBytes());
        List<string> args = ["crap", coverage, "--metrics", metrics, "--exclude-generated", "--format", "json"];
        if (keepProgram) args.AddRange(["--keep", "Program.cs"]);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await DotCovCli.RunAsync(args.ToArray(), stdout, stderr);

        await Assert.That(exit).IsEqualTo(expectedExit);
        using var json = JsonDocument.Parse(stdout.ToString());
        await Assert.That(json.RootElement.GetProperty("gate").GetProperty("outcome").GetString()).IsEqualTo(expectedOutcome);
        await Assert.That(json.RootElement.GetProperty("methods").GetArrayLength()).IsEqualTo(keepProgram ? 2 : 1);
        if (missingExecutable)
            await Assert.That(stderr.ToString()).Contains("1 metrics members without coverage data");
    }

    private static MethodCoverageReport KeptCoverage()
    {
        using var input = new MemoryStream(Cobertura.NewDoc()
            .AddClass("src/A.cs", "App.A", c => c.Method("Kept", "()", "1", m => m.Line(1, 1))).ToBytes());
        return CoberturaParser.ParseMethods(input);
    }
}
