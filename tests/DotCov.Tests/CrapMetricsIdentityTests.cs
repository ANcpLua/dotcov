using DotCov.Tests.Infrastructure;
using DotCov.Tool;

namespace DotCov.Tests;
[TestClass]
public sealed class CrapMetricsIdentityTests
{
    [TestMethod]
    public void LongNonGenericIdentifier_StaysWithinLinearSignatureProcessing()
    {
        var identifier = new string ('A', 16_000);
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("M", $"({identifier})", "1", m => m.Line(1, 1))).ToBytes());
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input));
        Assert.AreEqual(GateOutcome.Pass, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OversizedSignature_IsRejectedThroughCli(bool metricsTooLong)
    {
        using var ws = TempWorkspace.Create("dotcov-signature-limit-");
        var identifier = new string ('A', 16_385);
        var coverage = ws.Write("coverage.xml", Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("M", metricsTooLong ? "()" : $"({identifier})", "1", m => m.Line(1, 1))).ToBytes());
        var metrics = ws.Write("metrics.xml", CodeMetricsReportBuilder.Create().Namespace("App", t => t.Type("A", m => m.Method($"void A.M({identifier} value)", 2, 1))).ToBytes());
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        string[] args = metricsTooLong ? ["crap", coverage, "--metrics", metrics] : ["crap", coverage];
        var exitCode = await DotCovCli.RunAsync(args, stdout, stderr);
        Assert.AreEqual(DotCovCli.ErrorExitCode, exitCode);
        Assert.Contains("exceeds 16384 characters", stderr.ToString());
        Assert.Contains(metricsTooLong ? metrics : coverage, stderr.ToString());
        Assert.Contains(metricsTooLong ? "A.M(" : "App.A.M", stderr.ToString());
    }

    [TestMethod]
    public async Task MalformedMetricsTuple_NamesTheFileAndMemberThroughCli()
    {
        using var ws = TempWorkspace.Create("dotcov-metrics-tuple-");
        var coverage = ws.Write("coverage.xml", Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("M", "()", "1", m => m.Line(1, 1))).ToBytes());
        var deep = new string ('(', 65) + "int" + string.Concat(Enumerable.Repeat(",int)", 65));
        var metrics = ws.Write("metrics.xml", CodeMetricsReportBuilder.Create().Namespace("App", t => t.Type("A", m => m.Method($"void A.M({deep} value)", 2, 1))).ToBytes());
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exitCode = await DotCovCli.RunAsync(["crap", coverage, "--metrics", metrics], stdout, stderr);
        Assert.AreEqual(DotCovCli.ErrorExitCode, exitCode);
        Assert.Contains($"{metrics}: Metrics member display 'void A.M(((", stderr.ToString());
        Assert.Contains("Tuple nesting exceeds 64 levels.", stderr.ToString());
    }

    [TestMethod]
    [DataRow("App.A/<M>d__0", "MoveNext", "()", true)]
    [DataRow("App.A", "M", "()", false)]
    [DataRow("App.A", "M", "unknown", true)]
    public void SourceScopedCandidates_HandleMissingOriginArity(string type, string method, string signature, bool matched)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", type, c => c.Method(method, signature, null, m => m.Line(1, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("void A.M(int x)", 2, 1)).Select(m => m with { File = "src/A.cs" }).ToArray();
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.HasCount(matched ? 0 : 1, report.UnmatchedMetricsMembers);
        Assert.AreEqual(matched ? GateOutcome.Pass : GateOutcome.NoData, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    public void LegacyMetricsWithoutADeclaringType_StayUnmatched()
    {
        CodeMetricsMember[] metrics = [new("App.Absent", "M", CodeMetricsMemberKind.Method, 0, 2, "void Absent.M()"), new("App.Absent", "P", CodeMetricsMemberKind.Property, null, 2, "int Absent.P")];
        var report = CrapAnalysis.Analyze(Array.Empty<MethodCoverage>(), metrics);
        Assert.HasCount(1, report.UnmatchedMetricsMembers);
        Assert.AreEqual(GateOutcome.NoData, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    public void ExplicitEmptySourceRoot_LeavesRelativeMetricsPathsUnchanged()
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("M", "()", null, m => m.Line(1, 1))).ToBytes());
        var parsed = CoberturaParser.ParseMethods(input);
        var coverage = new MethodCoverageReport(parsed.Methods, [], [""]);
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("void A.M()", 2, 1)).Select(m => m with { File = "src/A.cs" }).ToArray();
        Assert.AreEqual(GateOutcome.Pass, CrapAnalysis.Analyze(coverage, metrics).Evaluate(30).Outcome);
    }

    [TestMethod]
    public void SourceScopedAndSourceLessOverloads_CombineWithoutLosingIdentity()
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("M", "(System.Int32)", null, m => m.Line(1, 1)).Method("M", "(System.Boolean)", null, m => m.Line(2, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("void A.M(int x)", 2, 1).Method("void A.M(bool x)", 3, 1)).Select((m, i) => m with { File = i == 0 ? "src/A.cs" : null }).ToArray();
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.IsEmpty(report.UnmatchedMetricsMembers);
        Assert.AreSequenceEqual([2, 3], report.Methods.Select(m => m.Complexity), SequenceOrder.InAnyOrder);
        Assert.AreEqual(GateOutcome.Pass, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    [DataRow("One.List`1<System.Int32>", "Two.List`1<System.Int32>")]
    [DataRow("List`1<System.Int32>", "Two.List`1<System.Int32>")]
    public void GenericQualificationCollisions_CannotProveCompleteCoverage(string first, string second)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("M", $"({first})", null, m => m.Line(1, 1)).Method("M", $"({second})", null, m => m.Line(2, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("void A.M(List<int> x)", 2, 1).Method("void A.M(Two.List<int> x)", 3, 1));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.AreEqual(GateOutcome.NoData, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    public void ConstructedGenericArguments_KeepNestedTypesArraysAndReferences()
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("M", "(System.Collections.Generic.Dictionary`2<System.String,System.Collections.Generic.List`1<System.Int32[]>>&)", null, m => m.Line(1, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("void A.M(ref Dictionary<string,List<int[]>> x)", 2, 1));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.IsEmpty(report.UnmatchedMetricsMembers);
        Assert.AreEqual(GateOutcome.Pass, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ConflictingSingleSignature_CannotBorrowAnotherMethodsMeasurement(bool includeMatch)
    {
        var doc = Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c =>
        {
            c.Method("M", "(System.String)", null, m => m.Line(1, 1));
            if (includeMatch)
                c.Method("M", "(System.Int32)", null, m => m.Line(2, 1));
        });
        using var input = new MemoryStream(doc.ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("void A.M(int x)", 2, 1));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.HasCount(1, report.Unscored);
        Assert.HasCount(includeMatch ? 0 : 1, report.UnmatchedMetricsMembers);
        Assert.AreEqual(GateOutcome.NoData, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SourceIdentity_MatchesSeparateFilesWithoutReusingRows(bool includeSecondMetrics)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("other/A.cs", "App.A", c => c.Method("M", "()", null, m => m.Line(1, 1))).AddClass("src/A.cs", "App.A", c => c.Method("M", "()", null, m => m.Line(2, 1))).ToBytes());
        var first = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("void A.M()", 2, 1)).Select(m => m with { File = "src/A.cs" });
        var second = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("void A.M()", 7, 1)).Select(m => m with { File = "other/A.cs" });
        var metrics = (includeSecondMetrics ? first.Concat(second) : first).ToArray();
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.HasCount(includeSecondMetrics ? 0 : 1, report.Unscored);
        Assert.AreSequenceEqual(includeSecondMetrics ? [7, 2] : [2], report.Methods.Select(m => m.Complexity), SequenceOrder.InAnyOrder);
        Assert.AreEqual(includeSecondMetrics ? GateOutcome.Pass : GateOutcome.NoData, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    [DataRow(null, "src/./A.cs", "src/A.cs")]
    [DataRow(".", "src/A.cs", "src/A.cs")]
    [DataRow("/", "src/A.cs", "src/A.cs")]
    [DataRow("/repo", "src/A.cs", "src/A.cs")]
    [DataRow("/repo", "/repo/src/./A.cs", "src/A.cs")]
    [DataRow("c:/repo", "c:\\repo\\src\\A.cs", "src/A.cs")]
    [DataRow("c:/repo", "src/A.cs", "src/A.cs")]
    [DataRow("/repo", "A", "A")]
    public void EquivalentSourceConventions_Match(string? root, string metricsFile, string coverageFile)
    {
        var doc = Cobertura.NewDoc().AddClass(coverageFile, "App.A", c => c.Method("M", "()", null, m => m.Line(1, 1)));
        if (root is not null)
            doc.WithSource(root);
        using var input = new MemoryStream(doc.ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("void A.M()", 2, 1)).Select(m => m with { File = metricsFile }).ToArray();
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.IsEmpty(report.UnmatchedMetricsMembers);
        Assert.AreEqual(GateOutcome.Pass, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SourceBearingPropertyFallback_StaysWithinItsFile(bool rightFile)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass(rightFile ? "src/A.cs" : "other/A.cs", "App.A", c => c.Method("get_P", "()", null, m => m.Line(1, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Property("int A.P", 2)).Select(m => m with { File = "src/A.cs" }).ToArray();
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.AreEqual(rightFile ? GateOutcome.Pass : GateOutcome.NoData, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    public void KnownSourceWithDifferentMember_CannotProvidePropertyFallback()
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("get_Missing", "()", null, m => m.Line(1, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("void A.M()", 2, 1)).Select(m => m with { File = "src/A.cs" }).ToArray();
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.HasCount(1, report.UnmatchedMetricsMembers);
        Assert.AreEqual(GateOutcome.NoData, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void LegacyGenericMetrics_OnlyRecoverUnambiguousTypeIdentity(bool ambiguous)
    {
        var doc = Cobertura.NewDoc().AddClass("src/A.cs", "App.Result`1", c => c.Method("M", "()", null, m => m.Line(1, 1)));
        if (ambiguous)
            doc.AddClass("src/B.cs", "App.Result", c => c.Method("M", "()", null, m => m.Line(2, 1)));
        using var input = new MemoryStream(doc.ToBytes());
        CodeMetricsMember[] metrics = [new("App.Result", "M", CodeMetricsMemberKind.Method, 0, 2, "void Result.M()")];
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.HasCount(ambiguous ? 1 : 0, report.Unscored);
        Assert.AreEqual(ambiguous ? GateOutcome.NoData : GateOutcome.Pass, report.Evaluate(30).Outcome);
    }
}
