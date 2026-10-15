using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;
[TestClass]
public sealed class CrapTypeAndConstructorTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void GenericAndNonGenericTypes_KeepSeparateMetrics(bool exclude)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/Kept.cs", "App.Kept", c => c.Method("M", "()", "1", m => m.Line(1, 1))).AddClass("src/A.g.cs", "App.Result", c => c.Method("M", "()", null, m => m.Line(2, 1))).AddClass("src/A.g.cs", "App.Result`1", c => c.Method("M", "()", null, m => m.Line(3, 1))).AddClass("src/A.g.cs", "App.Outer`1/Inner`2", c => c.Method("M", "()", null, m => m.Line(4, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.Create().Namespace("App", types => types.Type("Result", m => m.Method("void Result.M()", 2, 1)).Type("Result<T>", m => m.Method("void Result<T>.M()", 7, 1)).Type("Outer<T>.Inner<U, V>", m => m.Method("void Inner<U, V>.M()", 9, 1))).Parse();
        var coverage = CoberturaParser.ParseMethods(input);
        if (exclude)
            coverage = coverage.Exclude([".g.cs"], []);
        var report = CrapAnalysis.Analyze(coverage, metrics);
        Assert.IsEmpty(report.UnmatchedMetricsMembers);
        Assert.IsEmpty(report.Unscored);
        Assert.AreSequenceEqual(exclude ? [1] : [1, 2, 7, 9], report.Methods.Select(m => m.Complexity), SequenceOrder.InAnyOrder);
        Assert.AreEqual(GateOutcome.Pass, report.Evaluate(30).Outcome);
        Assert.AreEqual("App.Result", metrics[1].TypeName);
        Assert.AreEqual("App.Result`1", metrics[1].TypeIdentity);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("UNKNOWN")]
    [DataRow("src/./A.cs")]
    public void CompleteConstructorPair_IsMatchedWithConservativeComplexity(string? source)
    {
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("A.A()", 2, 1).Method("A.A()", 7, 1).Method("A.A(int x)", 3, 1)).Select(m => m with { File = source }).ToArray();
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method(".ctor", "()", null, m => m.Line(1, 1)).Method(".cctor", "()", null, m => m.Line(2, 1)).Method(".ctor", "(System.Int32)", null, m => m.Line(3, 1))).ToBytes());
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.IsEmpty(report.UnmatchedMetricsMembers);
        Assert.AreSequenceEqual([7, 7, 3], report.Methods.Select(m => m.Complexity), SequenceOrder.InAnyOrder);
        Assert.AreEqual(GateOutcome.Pass, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    [DataRow(null, false)]
    [DataRow(null, true)]
    [DataRow("UNKNOWN", false)]
    [DataRow("UNKNOWN", true)]
    [DataRow("src/A.cs", false)]
    [DataRow("src/A.cs", true)]
    public void ConstructorPair_CannotBeReusedAcrossFiles(string? source, bool secondPair)
    {
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("A.A()", 2, 1).Method("A.A()", 7, 1)).Select(m => m with { File = source }).ToArray();
        var doc = Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method(".ctor", "()", null, m => m.Line(1, 1)).Method(".cctor", "()", null, m => m.Line(2, 1))).AddClass("other/A.cs", "App.A", c =>
        {
            c.Method(".ctor", "()", null, m => m.Line(3, 1));
            if (secondPair)
                c.Method(".cctor", "()", null, m => m.Line(4, 1));
        });
        using var input = new MemoryStream(doc.ToBytes());
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.AreEqual(2, report.UnmatchedMetricsMembers.Count);
        Assert.AreEqual(GateOutcome.NoData, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("no-lines")]
    [DataRow("other-file")]
    [DataRow("duplicate-instance")]
    [DataRow("wrong-source")]
    [DataRow("extra-metrics")]
    [DataRow("unknown-signature")]
    public void IncompleteOrAmbiguousConstructorPair_CannotClearMetrics(string scenario)
    {
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m =>
        {
            m.Method("A.A()", 2, 1).Method(scenario == "unknown-signature" ? "A.A" : "A.A()", 7, 1);
            if (scenario == "extra-metrics")
                m.Method("A.A()", 4, 1);
        }).Select(m => m with { File = scenario == "wrong-source" ? "other/A.cs" : null }).ToArray();
        var doc = Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method(".ctor", "()", "1", m => m.Line(1, 1)));
        if (scenario != "missing")
            doc.AddClass(scenario is "other-file" or "duplicate-instance" ? "other/A.cs" : "src/A.cs", "App.A", c => c.Method(scenario == "duplicate-instance" ? ".ctor" : ".cctor", "()", "1", m =>
            {
                if (scenario != "no-lines")
                    m.Line(2, 1);
            }));
        using var input = new MemoryStream(doc.ToBytes());
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.IsNotEmpty(report.UnmatchedMetricsMembers);
        Assert.AreEqual(GateOutcome.NoData, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    [DataRow("Ext.extension<T>(T)", "App.Ext")]
    [DataRow("Outer<T>.Ext.extension<U>(U)", "App.Outer`1.Ext")]
    public void GenericExtensionBlock_StaticMemberBelongsToItsEnclosingType(string owner, string identity)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/Ext.cs", identity, c => c.Method("Make", "()", null, m => m.Line(1, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", owner, m => m.Method("int extension<T>(T).Make()", 2, 1));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.AreEqual(identity, metrics.Single().TypeIdentity);
        Assert.IsEmpty(report.UnmatchedMetricsMembers);
        Assert.IsEmpty(report.Unscored);
        Assert.AreEqual(GateOutcome.Pass, report.Evaluate(30).Outcome);
    }
}
