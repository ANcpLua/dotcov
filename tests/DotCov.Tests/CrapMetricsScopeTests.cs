using System.Text;
using System.Text.Json;
using DotCov.Tests.Infrastructure;
using DotCov.Tool;

namespace DotCov.Tests;

public sealed class CrapMetricsScopeTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OverloadedIndexers_UseAccessorParameterCountsForCompleteness(bool missingSetter)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c =>
        {
            c.Method("get_Item", "(System.Int32)", null, m => m.Line(1, 1));
            c.Method("set_Item", "(System.Int32,System.String)", null, m => m.Line(2, 1));
            c.Method("get_Item", "(System.Int32,System.Int32)", null, m => m.Line(3, 1));
            if (!missingSetter)
                c.Method("set_Item", "(System.Int32,System.Int32,System.String)", null, m => m.Line(4, 1));
        }).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m
            .Property("string A.this[int x]", 2, a => a
                .Method("string A.this[int x].get", 1, 1)
                .Method("void A.this[int x].set", 1, 1))
            .Property("string A.this[int x, int y]", 2, a => a
                .Method("string A.this[int x, int y].get", 1, 1)
                .Method("void A.this[int x, int y].set", 1, 1)));

        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);

        await Assert.That(report.Methods.Count).IsEqualTo(missingSetter ? 3 : 4);
        await Assert.That(report.Unscored).IsEmpty();
        await Assert.That(report.UnmatchedMetricsMembers).IsEquivalentTo(missingSetter
            ? ["void A.this[int x, int y].set"] : Array.Empty<string>());
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(missingSetter ? GateOutcome.NoData : GateOutcome.Pass);
    }

    [Test]
    [Arguments(0, 0, GateOutcome.NoData)]
    [Arguments(0, 1, GateOutcome.Pass)]
    [Arguments(1, 0, GateOutcome.Pass)]
    [Arguments(1, 1, GateOutcome.NoData)]
    public async Task UnknownAndExactArityBuckets_CombineExecutableIdentityAndComplexity(
        int unknownLines, int exactLines, GateOutcome expected)
    {
        using var input = new MemoryStream(Cobertura.NewDoc()
            .AddClass("src/A.cs", "App.A", c => c.Method("M", "(System.Int32)", null, m => m.Line(1, 1)))
            .ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m
            .Method("void A.M", 7, unknownLines).Method("void A.M(int x)", 9, exactLines));

        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);

        await Assert.That(report.Methods.Count).IsEqualTo(unknownLines + exactLines == 0 ? 0 : 1);
        if (report.Methods.Count > 0)
            await Assert.That(report.Methods.Single().Complexity).IsEqualTo(exactLines > 0 ? 9 : 7);
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(expected);
        await Assert.That(report.UnmatchedMetricsMembers.Count).IsEqualTo(unknownLines + exactLines == 2 ? 2 : 0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RepeatedMeasuredArities_ReuseLongDisplaySummaries(bool extension)
    {
        const int count = 1000;
        var padding = new string('x', 2048);
        var owner = extension ? "extension(string)" : "Ext";
        var metrics = CodeMetricsReportBuilder.ParseType("App", extension ? $"Ext.{owner}" : owner, m =>
        {
            for (var i = 0; i < count; i++)
                m.Method($"int {owner}.M(string x = \"{padding}{i}\")", 9, 1);
        });
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/Ext.cs", "App.Ext", c =>
        {
            for (var i = 0; i < count; i++)
                c.Method("M", $"(App.Arg{i},System.String)", null, m => m.Line(i + 1, 1));
        }).ToBytes());

        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);

        await Assert.That(report.Methods.Count).IsEqualTo(count);
        await Assert.That(report.Methods.Select(m => m.Complexity).Distinct()).IsEquivalentTo([9]);
        await Assert.That(report.UnmatchedMetricsMembers.Count).IsEqualTo(count);
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.NoData);
    }

    [Test]
    public async Task RepeatedExcludedIdentities_KeepDistinctAritiesAndMissingExecutableMembers()
    {
        var coverage = Cobertura.NewDoc()
            .AddClass("src/A.cs", "App.A", c => c.Method("Kept", "()", "1", m => m.Line(1, 1)))
            .AddClass("src/A.g.cs", "App.A", c =>
            {
                for (var i = 0; i < 1000; i++)
                    c.Method("M", $"(App.Arg{i})", "1", m => m.Line(2, 1));
                c.Method("M", "(System.Int32,System.Int32)", "1", m => m.Line(3, 1));
            });
        using var input = new MemoryStream(coverage.ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m =>
        {
            for (var i = 0; i < 1000; i++) m.Method($"void A.M(Arg{i} x)", 1, 0);
            m.Method("void A.M(int x, int y)", 1, 1);
            m.Method("void A.M(int x, int y, int z)", 1, 1);
        });

        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input).Exclude([".g.cs"], []), metrics);

        await Assert.That(report.Methods.Count).IsEqualTo(1);
        await Assert.That(report.UnmatchedMetricsMembers).IsEquivalentTo(["void A.M(int x, int y, int z)"]);
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.NoData);
    }

    [Test]
    [Arguments(false, "void A.M(int x)")]
    [Arguments(true, "void A.M(int x)")]
    [Arguments(false, "void A.M(string x = \" extension(string).M\")")]
    public async Task DifferentKnownArities_CannotEstablishCoverage(bool embeddedComplexity, string display)
    {
        using var input = new MemoryStream(Cobertura.NewDoc()
            .AddClass("src/A.cs", "App.A", c => c.Method("M", "(System.Int32,System.Int32)",
                embeddedComplexity ? "1" : null, m => m.Line(1, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method(display, 9, 1));

        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);

        await Assert.That(report.Methods.Single().Complexity).IsEqualTo(embeddedComplexity ? 1 : 9);
        await Assert.That(report.UnmatchedMetricsMembers).IsEquivalentTo([display]);
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.NoData);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task ExtensionReceiver_WithoutInstanceMetadata_CannotProveAMatch(bool exclude, bool generic)
    {
        using var input = new MemoryStream(Cobertura.NewDoc()
            .AddClass("src/A.cs", "App.A", c => c.Method("Kept", "()", "1", m => m.Line(1, 1)))
            .AddClass("src/Ext.g.cs", "App.Ext", c => c.Method("M", "(System.String,System.Int32)",
                "1", m => m.Line(2, 1))).ToBytes());
        var coverage = CoberturaParser.ParseMethods(input);
        if (exclude) coverage = coverage.Exclude([".g.cs"], []);
        var extension = generic ? "extension<T>(T)" : "extension(string)";
        var metrics = CodeMetricsReportBuilder.ParseType("App", $"Ext.{extension}", m => m
            .Method($"int {extension}.M(int x)", 1, 1));

        var report = CrapAnalysis.Analyze(coverage, metrics);

        await Assert.That(report.UnmatchedMetricsMembers.Count).IsEqualTo(1);
        await Assert.That(report.Methods.Count).IsEqualTo(exclude ? 1 : 2);
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.NoData);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MeasuredOverload_CannotConsumeItsUnmeasuredSibling(bool embeddedComplexity)
    {
        using var input = new MemoryStream(Cobertura.NewDoc()
            .AddClass("src/A.cs", "App.A", c => c.Method("M", "(System.Int32)",
                embeddedComplexity ? "1" : null, m => m.Line(1, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m
            .Method("void A.M(int x)", 1, 1).Method("void A.M(string x)", 9, 1));

        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);

        await Assert.That(report.Methods.Single().Complexity).IsEqualTo(embeddedComplexity ? 1 : 9);
        await Assert.That(report.UnmatchedMetricsMembers)
            .IsEquivalentTo(["void A.M(int x)", "void A.M(string x)"]);
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.NoData);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ZeroBodyOverload_DoesNotMakeExecutableMatchAmbiguous(bool exclude)
    {
        using var input = new MemoryStream(Cobertura.NewDoc()
            .AddClass("src/A.cs", "App.A", c => c.Method("Kept", "()", "1", m => m.Line(1, 1)))
            .AddClass("src/A.g.cs", "App.A", c => c.Method("M", "(System.String)", "1", m => m.Line(2, 1)))
            .ToBytes());
        var coverage = CoberturaParser.ParseMethods(input);
        if (exclude) coverage = coverage.Exclude([".g.cs"], []);
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m
            .Method("void A.M(int x)", 1, 0).Method("void A.M(string x)", 9, 1));

        var report = CrapAnalysis.Analyze(coverage, metrics);

        await Assert.That(report.UnmatchedMetricsMembers).IsEmpty();
        await Assert.That(report.Methods.Count).IsEqualTo(exclude ? 1 : 2);
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.Pass);
    }

    [Test]
    public async Task Cli_MeasuredSameArityOverload_CannotPassWithAnUninstrumentedSibling()
    {
        using var ws = TempWorkspace.Create("dotcov-crap-measured-overload-");
        var coverage = ws.Write("coverage.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/A.cs", "App.A", c => c.Method("M", "(System.Int32)", "1", m => m.Line(1, 1)))
            .ToBytes());
        var metrics = ws.Write("metrics.xml", CodeMetricsReportBuilder.Create().Namespace("App", t => t
            .Type("A", m => m.Method("void A.M(int x)", 1, 1).Method("void A.M(string x)", 9, 1))).ToBytes());
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await DotCovCli.RunAsync(
            ["crap", coverage, "--metrics", metrics, "--format", "json"], stdout, stderr);

        await Assert.That(exit).IsEqualTo(DotCovCli.NoDataExitCode);
        using var json = JsonDocument.Parse(stdout.ToString());
        await Assert.That(json.RootElement.GetProperty("gate").GetProperty("outcome").GetString()).IsEqualTo("nodata");
        await Assert.That(stderr.ToString()).Contains("2 metrics members without coverage data");
    }

    [Test]
    [Arguments("(System.Int32)", "void A.M(int x)", "void A.M(string x)", 2)]
    [Arguments("", "void A.M(int x)", "void A.M(string x)", 2)]
    [Arguments("(System.Int32)", "void A.M(int x)", "void A.M(int x, int y)", 1)]
    [Arguments("(System.Int32)", "void A.M(int x, int y)", "void A.Other()", 2)]
    [Arguments("(System.Int32)", "void A.M", "void A.Other()", 1)]
    public async Task ExcludedOverloads_OnlyConsumeUniqueCompatibleMetrics(
        string signature, string first, string second, int unmatched)
    {
        using var input = new MemoryStream(Cobertura.NewDoc()
            .AddClass("src/A.cs", "App.A", c => c.Method("Kept", "()", "1", m => m.Line(1, 1)))
            .AddClass("src/A.g.cs", "App.A", c => c.Method("M", signature, "1", m => m.Line(2, 1)))
            .ToBytes());
        var filtered = CoberturaParser.ParseMethods(input).Exclude([".g.cs"], []);
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m
            .Method(first, 1, 1).Method(second, 9, 1));

        var report = CrapAnalysis.Analyze(filtered, metrics);

        await Assert.That(report.UnmatchedMetricsMembers.Count).IsEqualTo(unmatched);
        await Assert.That(report.UnmatchedMetricsMembers).Contains(second);
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.NoData);
        await Assert.That(report.Methods.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ExcludedMethodWithNoMetricsMatch_DoesNotHideAnotherMember()
    {
        using var input = new MemoryStream(Cobertura.NewDoc()
            .AddClass("src/A.cs", "App.A", c => c.Method("Kept", "()", "1", m => m.Line(1, 1)))
            .AddClass("src/A.g.cs", "App.A", c => c.Method("Generated", "()", "1", m => m.Line(2, 1)))
            .ToBytes());
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input).Exclude([".g.cs"], []),
            CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("void A.Missing()", 1, 1)));

        await Assert.That(report.UnmatchedMetricsMembers).IsEquivalentTo(["void A.Missing()"]);
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.NoData);
    }

    [Test]
    public async Task Cli_ExcludedSameArityOverload_CannotHideAnUninstrumentedSibling()
    {
        using var ws = TempWorkspace.Create("dotcov-crap-overload-scope-");
        var coverage = ws.Write("coverage.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/A.cs", "App.A", c => c.Method("Kept", "()", "1", m => m.Line(1, 1)))
            .AddClass("src/A.g.cs", "App.A", c => c.Method("M", "(System.Int32)", "1", m => m.Line(2, 1)))
            .ToBytes());
        var metrics = ws.Write("metrics.xml", CodeMetricsReportBuilder.Create().Namespace("App", t => t
            .Type("A", m => m.Method("void A.M(int x)", 1, 1).Method("void A.M(string x)", 9, 1))).ToBytes());
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        var exit = await DotCovCli.RunAsync(
            ["crap", coverage, "--metrics", metrics, "--exclude-generated", "--format", "json"], stdout, stderr);

        await Assert.That(exit).IsEqualTo(DotCovCli.NoDataExitCode);
        using var json = JsonDocument.Parse(stdout.ToString());
        await Assert.That(json.RootElement.GetProperty("gate").GetProperty("outcome").GetString()).IsEqualTo("nodata");
        await Assert.That(stderr.ToString()).Contains("2 metrics members without coverage data");
    }

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

    [Test]
    [Arguments("()", GateOutcome.Pass)]
    [Arguments("(System.String)", GateOutcome.NoData)]
    public async Task StaticExtension_DoesNotInventAReceiver(string signature, GateOutcome expected)
    {
        using var input = new MemoryStream(Cobertura.NewDoc()
            .AddClass("src/Ext.cs", "App.Ext", c => c.Method("Make", signature, null, m => m.Line(1, 1)))
            .ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "Ext.extension(string)",
            m => m.Method("string extension(string).Make()", 1, 1));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(expected);
        await Assert.That(report.UnmatchedMetricsMembers.Count).IsEqualTo(expected is GateOutcome.Pass ? 0 : 1);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task ZeroBodyMetrics_CannotSupplyMeasuredComplexity(bool accessor, bool embedded)
    {
        using var input = new MemoryStream(Cobertura.NewDoc()
            .AddClass("src/A.cs", "App.A", c => c.Method(accessor ? "get_Value" : "M", "()",
                embedded ? "2" : null, m => m.Line(1, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m =>
        {
            if (accessor) m.Property("int A.Value", 1, a => a.Method("int A.Value.get", 1, 0));
            else m.Method("void A.M()", 1, 0);
        });
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        await Assert.That(report.Methods.Count).IsEqualTo(embedded ? 1 : 0);
        await Assert.That(report.Unscored.Count).IsEqualTo(embedded ? 0 : 1);
        await Assert.That(report.UnmatchedMetricsMembers).IsEmpty();
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(embedded ? GateOutcome.Pass : GateOutcome.NoData);
    }

    [Test]
    public async Task ZeroBodyPropertyAggregate_CannotSupplyMeasuredComplexity()
    {
        using var input = new MemoryStream(Cobertura.NewDoc()
            .AddClass("src/A.cs", "App.A", c => c.Method("get_Value", "()", null, m => m.Line(1, 1)))
            .ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Property("int A.Value", 1))
            .Select(m => m with { ExecutableLines = 0 }).ToArray();
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        await Assert.That(report.Unscored.Count).IsEqualTo(1);
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.NoData);
    }

    [Test]
    [Arguments("Program.cs", false, false, false)]
    [Arguments("src/A.g.cs", false, false, false)]
    [Arguments("c:\\repo\\obj\\Generated.cs", false, false, false)]
    [Arguments("src/A.g.cs", true, false, true)]
    [Arguments("src/A.g.cs", true, true, false)]
    [Arguments("src/A.cs", false, false, true)]
    [Arguments("", false, false, true)]
    [Arguments("UNKNOWN", false, false, true)]
    [Arguments(null, false, false, true)]
    public async Task MetricsOnlyFiles_RespectExclusionOperations(
        string? file, bool keep, bool excludeAgain, bool missing)
    {
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m
            .Method("void A.First()", 1, 1).Method("void A.Second()", 1, 1))
            .Select(m => m with { File = file }).ToArray();
        var original = KeptCoverage();
        var filtered = original.Exclude(ExclusionRules.WellKnown, keep ? ["A.g.cs"] : []);
        if (excludeAgain) filtered = filtered.Exclude([".g.cs"], []);
        var report = CrapAnalysis.Analyze(filtered, metrics);
        await Assert.That(report.UnmatchedMetricsMembers.Count).IsEqualTo(missing ? 2 : 0);
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(missing ? GateOutcome.NoData : GateOutcome.Pass);
        await Assert.That(report.Exclusion.ExcludedFiles.Count).IsEqualTo(missing ? 0 : 1);
        await Assert.That(CrapAnalysis.Analyze(original, metrics).UnmatchedMetricsMembers.Count).IsEqualTo(2);
    }

    [Test]
    public async Task LaterKeep_CannotUndoEarlierMetricsOnlyExclusion()
    {
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("void A.Missing()", 1, 1))
            .Select(m => m with { File = "src/A.g.cs" }).ToArray();
        var report = CrapAnalysis.Analyze(KeptCoverage().Exclude([".g.cs"], [])
            .Exclude([".cs"], [".g.cs", "A.cs"]), metrics);
        await Assert.That(report.UnmatchedMetricsMembers).IsEmpty();
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.Pass);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Cli_MetricsOnlySourcePath_IsReadAndExcluded(bool keep)
    {
        using var ws = TempWorkspace.Create("dotcov-crap-metrics-only-");
        var coverage = ws.Write("coverage.cobertura.xml", Cobertura.NewDoc()
            .AddClass("src/A.cs", "App.A", c => c.Method("Kept", "()", "1", m => m.Line(1, 1))).ToBytes());
        var xml = """
            <CodeMetricsReport><Namespace Name="App"><NamedType Name="A"><Members>
              <Method Name="void A.Missing()" File="src/Generated.g.cs"><Metrics>
                <Metric Name="ExecutableLines" Value="1" />
                <Metric Name="CyclomaticComplexity" Value="1" />
              </Metrics></Method>
            </Members></NamedType></Namespace></CodeMetricsReport>
            """;
        var metrics = ws.Write("metrics.xml", Encoding.UTF8.GetBytes(xml));
        await Assert.That(CodeMetricsReader.ParseFile(metrics).Single().File).IsEqualTo("src/Generated.g.cs");
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        List<string> args = ["crap", coverage, "--metrics", metrics, "--exclude-generated", "--format", "json"];
        if (keep) args.AddRange(["--keep", "Generated.g.cs"]);
        var exit = await DotCovCli.RunAsync(args.ToArray(), stdout, stderr);
        await Assert.That(exit).IsEqualTo(keep ? DotCovCli.NoDataExitCode : 0);
        using var json = JsonDocument.Parse(stdout.ToString());
        await Assert.That(json.RootElement.GetProperty("gate").GetProperty("outcome").GetString())
            .IsEqualTo(keep ? "nodata" : "pass");
    }

    private static MethodCoverageReport KeptCoverage()
    {
        using var input = new MemoryStream(Cobertura.NewDoc()
            .AddClass("src/A.cs", "App.A", c => c.Method("Kept", "()", "1", m => m.Line(1, 1))).ToBytes());
        return CoberturaParser.ParseMethods(input);
    }
}
