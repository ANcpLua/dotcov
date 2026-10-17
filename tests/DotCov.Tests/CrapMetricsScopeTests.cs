using System.Text;
using System.Text.Json;
using DotCov.Tests.Infrastructure;
using DotCov.Tool;

namespace DotCov.Tests;
[TestClass]
public sealed class CrapMetricsScopeTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OverloadedIndexers_UseAccessorParameterCountsForCompleteness(bool missingSetter)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c =>
        {
            c.Method("get_Item", "(System.Int32)", null, m => m.Line(1, 1));
            c.Method("set_Item", "(System.Int32,System.String)", null, m => m.Line(2, 1));
            c.Method("get_Item", "(System.Int32,System.Int32)", null, m => m.Line(3, 1));
            if (!missingSetter)
                c.Method("set_Item", "(System.Int32,System.Int32,System.String)", null, m => m.Line(4, 1));
        }).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Property("string A.this[int x]", 2, a => a.Method("string A.this[int x].get", 1, 1).Method("void A.this[int x].set", 1, 1)).Property("string A.this[int x, int y]", 2, a => a.Method("string A.this[int x, int y].get", 1, 1).Method("void A.this[int x, int y].set", 1, 1)));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.HasCount(missingSetter ? 3 : 4, report.Methods);
        Assert.IsEmpty(report.Unscored);
        Assert.AreSequenceEqual(missingSetter ? ["void A.this[int x, int y].set"] : Array.Empty<string>(), report.UnmatchedMetricsMembers, SequenceOrder.InAnyOrder);
        Assert.AreEqual(missingSetter ? GateOutcome.NoData : GateOutcome.Pass, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    [DataRow(0, 0, GateOutcome.NoData)]
    [DataRow(0, 1, GateOutcome.Pass)]
    [DataRow(1, 0, GateOutcome.Pass)]
    [DataRow(1, 1, GateOutcome.NoData)]
    public void UnknownAndExactArityBuckets_CombineExecutableIdentityAndComplexity(int unknownLines, int exactLines, GateOutcome expected)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("M", "(System.Int32)", null, m => m.Line(1, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("void A.M", 7, unknownLines).Method("void A.M(int x)", 9, exactLines));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.HasCount(unknownLines + exactLines == 0 ? 0 : 1, report.Methods);
        if (report.Methods.Count > 0)
            Assert.AreEqual(exactLines > 0 ? 9 : 7, Assert.ContainsSingle(report.Methods).Complexity);
        Assert.AreEqual(expected, report.Evaluate(30).Outcome);
        Assert.HasCount(unknownLines + exactLines == 2 ? 2 : 0, report.UnmatchedMetricsMembers);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RepeatedMeasuredArities_ReuseLongDisplaySummaries(bool extension)
    {
        const int count = 1000;
        var padding = new string ('x', 2048);
        var owner = extension ? "extension(string)" : "Ext";
        var metrics = CodeMetricsReportBuilder.ParseType("App", extension ? $"Ext.{owner}" : owner, m =>
        {
            for (var i = 0; i < count; i++)
                m.Method($"int {owner}.M(string x = \"{padding}{i}\")", 9, 1);
        });
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/Ext.cs", "App.Ext", c =>
        {
            for (var i = 0; i < count; i++)
            {
                var line = i + 1;
                c.Method("M", $"(App.Arg{i},System.String)", null, m => m.Line(line, 1));
            }
        }).ToBytes());
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.HasCount(count, report.Methods);
        Assert.AreSequenceEqual([9], report.Methods.Select(m => m.Complexity).Distinct(), SequenceOrder.InAnyOrder);
        Assert.HasCount(count, report.UnmatchedMetricsMembers);
        Assert.AreEqual(GateOutcome.NoData, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    public void RepeatedExcludedIdentities_KeepDistinctAritiesAndMissingExecutableMembers()
    {
        var coverage = Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("Kept", "()", "1", m => m.Line(1, 1))).AddClass("src/A.g.cs", "App.A", c =>
        {
            for (var i = 0; i < 1000; i++)
                c.Method("M", $"(App.Arg{i})", "1", m => m.Line(2, 1));
            c.Method("M", "(System.Int32,System.Int32)", "1", m => m.Line(3, 1));
        });
        using var input = new MemoryStream(coverage.ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m =>
        {
            for (var i = 0; i < 1000; i++)
                m.Method($"void A.M(Arg{i} x)", 1, 0);
            m.Method("void A.M(int x, int y)", 1, 1);
            m.Method("void A.M(int x, int y, int z)", 1, 1);
        });
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input).Exclude([".g.cs"], []), metrics);
        Assert.HasCount(1, report.Methods);
        Assert.AreSequenceEqual(["void A.M(int x, int y, int z)"], report.UnmatchedMetricsMembers, SequenceOrder.InAnyOrder);
        Assert.AreEqual(GateOutcome.NoData, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    [DataRow(false, "void A.M(int x)")]
    [DataRow(true, "void A.M(int x)")]
    [DataRow(false, "void A.M(string x = \" extension(string).M\")")]
    public void DifferentKnownArities_CannotEstablishCoverage(bool embeddedComplexity, string display)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("M", "(System.Int32,System.Int32)", embeddedComplexity ? "1" : null, m => m.Line(1, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method(display, 9, 1));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.AreEqual(embeddedComplexity ? 1 : 9, Assert.ContainsSingle(report.Methods).Complexity);
        Assert.AreSequenceEqual([display], report.UnmatchedMetricsMembers, SequenceOrder.InAnyOrder);
        Assert.AreEqual(GateOutcome.NoData, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void ExtensionReceiver_WithoutInstanceMetadata_CannotProveAMatch(bool exclude, bool generic)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("Kept", "()", "1", m => m.Line(1, 1))).AddClass("src/Ext.g.cs", "App.Ext", c => c.Method("M", "(System.String,System.Int32)", "1", m => m.Line(2, 1))).ToBytes());
        var coverage = CoberturaParser.ParseMethods(input);
        if (exclude)
            coverage = coverage.Exclude([".g.cs"], []);
        var extension = generic ? "extension<T>(T)" : "extension(string)";
        var metrics = CodeMetricsReportBuilder.ParseType("App", $"Ext.{extension}", m => m.Method($"int {extension}.M(int x)", 1, 1));
        var report = CrapAnalysis.Analyze(coverage, metrics);
        Assert.HasCount(1, report.UnmatchedMetricsMembers);
        Assert.HasCount(exclude ? 1 : 2, report.Methods);
        Assert.AreEqual(GateOutcome.NoData, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MeasuredOverload_CannotConsumeItsUnmeasuredSibling(bool embeddedComplexity)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("M", "(System.Int32)", embeddedComplexity ? "1" : null, m => m.Line(1, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("void A.M(int x)", 1, 1).Method("void A.M(string x)", 9, 1));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.AreEqual(1, Assert.ContainsSingle(report.Methods).Complexity);
        Assert.AreSequenceEqual(["void A.M(string x)"], report.UnmatchedMetricsMembers, SequenceOrder.InAnyOrder);
        Assert.AreEqual(GateOutcome.NoData, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ZeroBodyOverload_DoesNotMakeExecutableMatchAmbiguous(bool exclude)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("Kept", "()", "1", m => m.Line(1, 1))).AddClass("src/A.g.cs", "App.A", c => c.Method("M", "(System.String)", "1", m => m.Line(2, 1))).ToBytes());
        var coverage = CoberturaParser.ParseMethods(input);
        if (exclude)
            coverage = coverage.Exclude([".g.cs"], []);
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("void A.M(int x)", 1, 0).Method("void A.M(string x)", 9, 1));
        var report = CrapAnalysis.Analyze(coverage, metrics);
        Assert.IsEmpty(report.UnmatchedMetricsMembers);
        Assert.HasCount(exclude ? 1 : 2, report.Methods);
        Assert.AreEqual(GateOutcome.Pass, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    public async Task Cli_MeasuredSameArityOverload_CannotPassWithAnUninstrumentedSibling()
    {
        using var ws = TempWorkspace.Create("dotcov-crap-measured-overload-");
        var coverage = ws.Write("coverage.cobertura.xml", Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("M", "(System.Int32)", "1", m => m.Line(1, 1))).ToBytes());
        var metrics = ws.Write("metrics.xml", CodeMetricsReportBuilder.Create().Namespace("App", t => t.Type("A", m => m.Method("void A.M(int x)", 1, 1).Method("void A.M(string x)", 9, 1))).ToBytes());
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await DotCovCli.RunAsync(["crap", coverage, "--metrics", metrics, "--format", "json"], stdout, stderr);
        Assert.AreEqual(DotCovCli.NoDataExitCode, exit);
        using var json = JsonDocument.Parse(stdout.ToString());
        Assert.AreEqual("nodata", json.RootElement.GetProperty("gate").GetProperty("outcome").GetString());
        Assert.Contains("1 metrics members without coverage data", stderr.ToString());
    }

    [TestMethod]
    [DataRow("(System.Int32)", "void A.M(int x)", "void A.M(string x)", 1)]
    [DataRow("", "void A.M(int x)", "void A.M(string x)", 2)]
    [DataRow("(System.Int32)", "void A.M(int x)", "void A.M(int x, int y)", 1)]
    [DataRow("(System.Int32)", "void A.M(int x, int y)", "void A.Other()", 2)]
    [DataRow("(System.Int32)", "void A.M", "void A.Other()", 1)]
    public void ExcludedOverloads_OnlyConsumeUniqueCompatibleMetrics(string signature, string first, string second, int unmatched)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("Kept", "()", "1", m => m.Line(1, 1))).AddClass("src/A.g.cs", "App.A", c => c.Method("M", signature, "1", m => m.Line(2, 1))).ToBytes());
        var filtered = CoberturaParser.ParseMethods(input).Exclude([".g.cs"], []);
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method(first, 1, 1).Method(second, 9, 1));
        var report = CrapAnalysis.Analyze(filtered, metrics);
        Assert.HasCount(unmatched, report.UnmatchedMetricsMembers);
        Assert.Contains(second, report.UnmatchedMetricsMembers);
        Assert.AreEqual(GateOutcome.NoData, report.Evaluate(30).Outcome);
        Assert.HasCount(1, report.Methods);
    }

    [TestMethod]
    public void ExcludedMethodWithNoMetricsMatch_DoesNotHideAnotherMember()
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("Kept", "()", "1", m => m.Line(1, 1))).AddClass("src/A.g.cs", "App.A", c => c.Method("Generated", "()", "1", m => m.Line(2, 1))).ToBytes());
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input).Exclude([".g.cs"], []), CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("void A.Missing()", 1, 1)));
        Assert.AreSequenceEqual(["void A.Missing()"], report.UnmatchedMetricsMembers, SequenceOrder.InAnyOrder);
        Assert.AreEqual(GateOutcome.NoData, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    public async Task Cli_ExcludedSameArityOverload_CannotHideAnUninstrumentedSibling()
    {
        using var ws = TempWorkspace.Create("dotcov-crap-overload-scope-");
        var coverage = ws.Write("coverage.cobertura.xml", Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("Kept", "()", "1", m => m.Line(1, 1))).AddClass("src/A.g.cs", "App.A", c => c.Method("M", "(System.Int32)", "1", m => m.Line(2, 1))).ToBytes());
        var metrics = ws.Write("metrics.xml", CodeMetricsReportBuilder.Create().Namespace("App", t => t.Type("A", m => m.Method("void A.M(int x)", 1, 1).Method("void A.M(string x)", 9, 1))).ToBytes());
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await DotCovCli.RunAsync(["crap", coverage, "--metrics", metrics, "--exclude-generated", "--format", "json"], stdout, stderr);
        Assert.AreEqual(DotCovCli.NoDataExitCode, exit);
        using var json = JsonDocument.Parse(stdout.ToString());
        Assert.AreEqual("nodata", json.RootElement.GetProperty("gate").GetProperty("outcome").GetString());
        Assert.Contains("1 metrics members without coverage data", stderr.ToString());
    }

    [TestMethod]
    [DataRow("0", 0)]
    [DataRow("3", 3)]
    [DataRow("-1", null)]
    [DataRow("unknown", null)]
    [DataRow("", null)]
    [DataRow("2147483648", null)]
    public void ExecutableLines_ArePreserved_AndOnlyExplicitZeroExemptsMissingCoverage(string value, int? expected)
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
        Assert.AreEqual(expected, Assert.ContainsSingle(metrics).ExecutableLines);
        var report = CrapAnalysis.Analyze(KeptCoverage(), metrics);
        Assert.AreEqual(expected is 0 ? GateOutcome.Pass : GateOutcome.NoData, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    [DataRow("0", null)]
    [DataRow("-3", null)]
    [DataRow("0", 4)]
    [DataRow("0", 0)]
    public void NonPositiveComplexity_IsNotAMeasurement_AndCannotPass(string complexity, int? executableLines)
    {
        var lines = executableLines is { } n ? $"""<Metric Name="ExecutableLines" Value="{n}" />""" : "";
        var xml = $"""
            <CodeMetricsReport><Namespace Name="App"><NamedType Name="A"><Members>
              <Method Name="void A.Kept()"><Metrics>
                <Metric Name="CyclomaticComplexity" Value="{complexity}" />
                {lines}
              </Metrics></Method>
            </Members></NamedType></Namespace></CodeMetricsReport>
            """;
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var metrics = CodeMetricsReader.Parse(input);
        using var coverage = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("Kept", "()", null, m => m.Line(1, 1))).ToBytes());
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(coverage), metrics);
        Assert.IsEmpty(report.Methods);
        Assert.HasCount(1, report.Unscored);
        Assert.AreEqual(GateOutcome.NoData, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    public void NonPositiveComplexity_StillCountsAsAnUnmatchedRow()
    {
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("void A.Kept()", 1, 1).Method("void A.Missing()", 0));
        var report = CrapAnalysis.Analyze(KeptCoverage(), metrics);
        Assert.AreSequenceEqual(["void A.Missing()"], report.UnmatchedMetricsMembers, SequenceOrder.InAnyOrder);
        Assert.AreEqual(GateOutcome.NoData, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("<Metric Name=\"CyclomaticComplexity\" Value=\"n/a\" />")]
    public void RowWithoutUsableComplexity_StillCountsAsUnmatched(string complexityMetric)
    {
        var xml = $"""
            <CodeMetricsReport><Namespace Name="App"><NamedType Name="A"><Members>
              <Method Name="void A.Kept()"><Metrics><Metric Name="CyclomaticComplexity" Value="1" /></Metrics></Method>
              <Method Name="void A.Missing()"><Metrics>{complexityMetric}<Metric Name="ExecutableLines" Value="3" /></Metrics></Method>
            </Members></NamedType></Namespace></CodeMetricsReport>
            """;
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var report = CrapAnalysis.Analyze(KeptCoverage(), CodeMetricsReader.Parse(input));
        Assert.AreSequenceEqual(["void A.Missing()"], report.UnmatchedMetricsMembers, SequenceOrder.InAnyOrder);
        Assert.AreEqual(GateOutcome.NoData, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    public void ExecutableLines_StayOnTheirOwnMember_AndDoNotEraseMeasuredBodies()
    {
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("void A.Kept()", 6, 0).Method("void A.InterfaceMethod()", 1, 0).Property("int A.AbstractValue", 2, a => a.Method("int A.AbstractValue.get", 1, 0).Method("void A.AbstractValue.set", 1, 0)).Method("void A.Unknown()", 1));
        Assert.IsNull(Assert.ContainsSingle(m => m.MemberName == "AbstractValue", metrics).ExecutableLines);
        Assert.IsNull(Assert.ContainsSingle(m => m.MemberName == "Unknown", metrics).ExecutableLines);
        var report = CrapAnalysis.Analyze(KeptCoverage(), metrics);
        Assert.AreSequenceEqual(["void A.Unknown()"], report.UnmatchedMetricsMembers, SequenceOrder.InAnyOrder);
        Assert.AreEqual("App.A.Kept", Assert.ContainsSingle(report.Methods).Method);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RepeatedExclusions_AccountForRemovedIdentities_WithoutScoringThem(bool withMetrics)
    {
        var coverage = Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("Kept", "()", "1", m => m.Line(1, 1))).AddClass("Program.cs", "App.Program", c => c.Method("Main", "()", null, _ =>
        {
        })).AddClass("tests/Test.cs", "App.Test", c => c.Method("Run", "()", "9", m => m.Line(1, 0))).AddClass("tests/Test.cs", "App.Test/<>c", c => c.Method("<Run>b__0_0", "()", "9", m => m.Line(2, 0)));
        using var input = new MemoryStream(coverage.ToBytes());
        var original = CoberturaParser.ParseMethods(input);
        var filtered = original.Exclude(["Program.cs"], []).Exclude(["tests/"], []);
        var metrics = CodeMetricsReportBuilder.Create().Namespace("App", t => t.Type("A", m => m.Method("void A.Kept()", 1)).Type("Program", m => m.Method("void Program.Main()", 9)).Type("Test", m => m.Method("void Test.Run()", 9))).Parse();
        var report = CrapAnalysis.Analyze(filtered, withMetrics ? metrics : null);
        Assert.AreEqual(GateOutcome.Pass, report.Evaluate(30).Outcome);
        Assert.HasCount(1, report.Methods);
        Assert.IsEmpty(report.Unscored);
        Assert.IsEmpty(report.UnmatchedMetricsMembers);
        Assert.HasCount(4, original.Methods);
        Assert.IsEmpty(original.Exclusion.ExcludedFiles);
    }

    [TestMethod]
    [DataRow(false, false, 0, "pass")]
    [DataRow(true, false, 1, "fail")]
    [DataRow(false, true, 3, "nodata")]
    public async Task Cli_ExclusionsAndDeclarations_RespectSelectedScope(bool keepProgram, bool missingExecutable, int expectedExit, string expectedOutcome)
    {
        using var ws = TempWorkspace.Create("dotcov-crap-scope-");
        var coverage = ws.Write("coverage.cobertura.xml", Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("Kept", "()", null, m => m.Line(1, 1))).AddClass("Program.cs", "App.Program/<Main>d__0", c => c.Method("MoveNext", "()", "9", m => m.Line(1, 0))).ToBytes());
        var metrics = ws.Write("metrics.xml", CodeMetricsReportBuilder.Create().Namespace("App", t => t.Type("A", m => m.Method("void A.Kept()", 1, 1).Method("void A.Missing()", 1, missingExecutable ? 1 : 0).Property("int A.AbstractValue", 1, a => a.Method("int A.AbstractValue.get", 1, 0))).Type("Program", m => m.Method("void Program.Main()", 9, 1))).ToBytes());
        List<string> args = ["crap", coverage, "--metrics", metrics, "--exclude-generated", "--format", "json"];
        if (keepProgram)
            args.AddRange(["--keep", "Program.cs"]);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = await DotCovCli.RunAsync(args.ToArray(), stdout, stderr);
        Assert.AreEqual(expectedExit, exit);
        using var json = JsonDocument.Parse(stdout.ToString());
        Assert.AreEqual(expectedOutcome, json.RootElement.GetProperty("gate").GetProperty("outcome").GetString());
        Assert.AreEqual(keepProgram ? 2 : 1, json.RootElement.GetProperty("methods").GetArrayLength());
        if (missingExecutable)
            Assert.Contains("1 metrics members without coverage data", stderr.ToString());
    }

    [TestMethod]
    [DataRow("()", GateOutcome.Pass)]
    [DataRow("(System.String)", GateOutcome.NoData)]
    public void StaticExtension_DoesNotInventAReceiver(string signature, GateOutcome expected)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/Ext.cs", "App.Ext", c => c.Method("Make", signature, null, m => m.Line(1, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "Ext.extension(string)", m => m.Method("string extension(string).Make()", 1, 1));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.AreEqual(expected, report.Evaluate(30).Outcome);
        Assert.HasCount(expected is GateOutcome.Pass ? 0 : 1, report.UnmatchedMetricsMembers);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void ZeroBodyMetrics_CannotSupplyMeasuredComplexity(bool accessor, bool embedded)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method(accessor ? "get_Value" : "M", "()", embedded ? "2" : null, m => m.Line(1, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m =>
        {
            if (accessor)
                m.Property("int A.Value", 1, a => a.Method("int A.Value.get", 1, 0));
            else
                m.Method("void A.M()", 1, 0);
        });
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.HasCount(embedded ? 1 : 0, report.Methods);
        Assert.HasCount(embedded ? 0 : 1, report.Unscored);
        Assert.IsEmpty(report.UnmatchedMetricsMembers);
        Assert.AreEqual(embedded ? GateOutcome.Pass : GateOutcome.NoData, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    public void ZeroBodyPropertyAggregate_CannotSupplyMeasuredComplexity()
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("get_Value", "()", null, m => m.Line(1, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Property("int A.Value", 1)).Select(m => m with { ExecutableLines = 0 }).ToArray();
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.HasCount(1, report.Unscored);
        Assert.AreEqual(GateOutcome.NoData, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    [DataRow("Program.cs", false, false, false)]
    [DataRow("src/A.g.cs", false, false, false)]
    [DataRow("c:\\repo\\obj\\Generated.cs", false, false, false)]
    [DataRow("src/A.g.cs", true, false, true)]
    [DataRow("src/A.g.cs", true, true, false)]
    [DataRow("src/A.cs", false, false, true)]
    [DataRow("", false, false, true)]
    [DataRow("UNKNOWN", false, false, true)]
    [DataRow(null, false, false, true)]
    public void MetricsOnlyFiles_RespectExclusionOperations(string? file, bool keep, bool excludeAgain, bool missing)
    {
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("void A.First()", 1, 1).Method("void A.Second()", 1, 1)).Select(m => m with { File = file }).ToArray();
        var original = KeptCoverage();
        var filtered = original.Exclude(ExclusionRules.WellKnown, keep ? ["A.g.cs"] : []);
        if (excludeAgain)
            filtered = filtered.Exclude([".g.cs"], []);
        var report = CrapAnalysis.Analyze(filtered, metrics);
        Assert.HasCount(missing ? 2 : 0, report.UnmatchedMetricsMembers);
        Assert.AreEqual(missing ? GateOutcome.NoData : GateOutcome.Pass, report.Evaluate(30).Outcome);
        Assert.HasCount(missing ? 0 : 1, report.Exclusion.ExcludedFiles);
        Assert.HasCount(2, CrapAnalysis.Analyze(original, metrics).UnmatchedMetricsMembers);
    }

    [TestMethod]
    public void LaterKeep_CannotUndoEarlierMetricsOnlyExclusion()
    {
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("void A.Missing()", 1, 1)).Select(m => m with { File = "src/A.g.cs" }).ToArray();
        var report = CrapAnalysis.Analyze(KeptCoverage().Exclude([".g.cs"], []).Exclude([".cs"], [".g.cs", "A.cs"]), metrics);
        Assert.IsEmpty(report.UnmatchedMetricsMembers);
        Assert.AreEqual(GateOutcome.Pass, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Cli_MetricsOnlySourcePath_IsReadAndExcluded(bool keep)
    {
        using var ws = TempWorkspace.Create("dotcov-crap-metrics-only-");
        var coverage = ws.Write("coverage.cobertura.xml", Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("Kept", "()", "1", m => m.Line(1, 1))).ToBytes());
        var xml = """
            <CodeMetricsReport><Namespace Name="App"><NamedType Name="A"><Members>
              <Method Name="void A.Missing()" File="src/Generated.g.cs"><Metrics>
                <Metric Name="ExecutableLines" Value="1" />
                <Metric Name="CyclomaticComplexity" Value="1" />
              </Metrics></Method>
            </Members></NamedType></Namespace></CodeMetricsReport>
            """;
        var metrics = ws.Write("metrics.xml", Encoding.UTF8.GetBytes(xml));
        Assert.AreEqual("src/Generated.g.cs", Assert.ContainsSingle(CodeMetricsReader.ParseFile(metrics)).File);
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        List<string> args = ["crap", coverage, "--metrics", metrics, "--exclude-generated", "--format", "json"];
        if (keep)
            args.AddRange(["--keep", "Generated.g.cs"]);
        var exit = await DotCovCli.RunAsync(args.ToArray(), stdout, stderr);
        Assert.AreEqual(keep ? DotCovCli.NoDataExitCode : 0, exit);
        using var json = JsonDocument.Parse(stdout.ToString());
        Assert.AreEqual(keep ? "nodata" : "pass", json.RootElement.GetProperty("gate").GetProperty("outcome").GetString());
    }

    [TestMethod]
    public void ExcludedCoverage_CannotConsumeMetricsFromAKnownKeptSource()
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("Kept", "()", "1", m => m.Line(1, 1))).AddClass("src/A.g.cs", "App.A", c => c.Method("M", "(System.Int32)", "1", m => m.Line(2, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("void A.M(string x)", 1, 1)).Select(m => m with { File = "src/A.cs" }).ToArray();
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input).Exclude([".g.cs"], []), metrics);
        Assert.AreSequenceEqual(["void A.M(string x)"], report.UnmatchedMetricsMembers, SequenceOrder.InAnyOrder);
        Assert.AreEqual(GateOutcome.NoData, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    [DataRow("0", null, false, 0)]
    [DataRow("2", null, false, 2)]
    [DataRow("-1", null, false, null)]
    [DataRow("invalid", null, false, null)]
    [DataRow("0", "2", false, 2)]
    [DataRow("0", "2", true, 2)]
    [DataRow("2", "0", false, 0)]
    [DataRow("2", "0", true, 0)]
    [DataRow("0", "invalid", false, null)]
    [DataRow("0", "invalid", true, null)]
    [DataRow("0", "-1", false, null)]
    public void LegacyExecutableLines_AreUsedOnlyWhenModernMetricIsAbsent(string legacy, string? modern, bool modernFirst, int? expected)
    {
        var legacyMetric = $"<Metric Name=\"LinesOfCode\" Value=\"{legacy}\" />";
        var modernMetric = modern is null ? "" : $"<Metric Name=\"ExecutableLines\" Value=\"{modern}\" />";
        var ordered = modernFirst ? modernMetric + legacyMetric : legacyMetric + modernMetric;
        var xml = $"""
            <CodeMetricsReport><Namespace Name="App"><NamedType Name="A"><Members>
              <Method Name="void A.Missing()"><Metrics>
                <Metric Name="CyclomaticComplexity" Value="1" />
                {ordered}
              </Metrics></Method>
            </Members></NamedType></Namespace></CodeMetricsReport>
            """;
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var metrics = CodeMetricsReader.Parse(input);
        Assert.AreEqual(expected, Assert.ContainsSingle(metrics).ExecutableLines);
        var report = CrapAnalysis.Analyze(KeptCoverage(), metrics);
        Assert.HasCount(expected is 0 ? 0 : 1, report.UnmatchedMetricsMembers);
        Assert.AreEqual(expected is 0 ? GateOutcome.Pass : GateOutcome.NoData, report.Evaluate(30).Outcome);
    }

    private static MethodCoverageReport KeptCoverage()
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("Kept", "()", "1", m => m.Line(1, 1))).ToBytes());
        return CoberturaParser.ParseMethods(input);
    }
}
