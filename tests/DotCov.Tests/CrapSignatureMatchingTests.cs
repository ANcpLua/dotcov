using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;
[TestClass]
public sealed class CrapSignatureMatchingTests
{
    [TestMethod]
    [DataRow("int x", "System.Int32")]
    [DataRow("dynamic x", "System.Object")]
    [DataRow("int? x", "System.Nullable`1<System.Int32>")]
    [DataRow("decimal?[] x", "System.Nullable`1<System.Decimal>[]")]
    [DataRow("ref long? x", "System.Nullable`1<System.Int64>&")]
    [DataRow("List<int?> x", "System.Collections.Generic.List`1<System.Nullable`1<System.Int32>>")]
    [DataRow("int x = 1", "System.Int32")]
    [DataRow("char x = ','", "System.Char")]
    [DataRow("string x = \"a,(b)=c\"", "System.String")]
    [DataRow("List<int> x", "System.Collections.Generic.List`1<System.Int32>")]
    [DataRow("(int,string) x", "System.ValueTuple`2<System.Int32,System.String>")]
    [DataRow("int[,] x", "System.Int32[,]")]
    [DataRow("params int[] x", "System.Int32[]")]
    [DataRow("ref int x", "System.Int32&")]
    [DataRow("out int x", "System.Int32&")]
    [DataRow("in int x", "System.Int32&")]
    [DataRow("global::App.Widget x", "App.Widget")]
    [DataRow("Widget x", "App.Widget")]
    [DataRow("string? x", "System.String")]
    [DataRow("object? x", "System.Object")]
    [DataRow("dynamic? x", "System.Object")]
    [DataRow("string?[]? x", "System.String[]")]
    [DataRow("int?[]? x", "System.Nullable`1<System.Int32>[]")]
    [DataRow("List<string?> x", "System.Collections.Generic.List`1<System.String>")]
    [DataRow("Widget[]? x", "App.Widget[]")]
    [DataRow("Inner x", "App.Outer/Inner")]
    [DataRow("Outer.Inner x", "App.Outer/Inner")]
    [DataRow("App.Outer.Inner x", "App.Outer+Inner")]
    [DataRow("Outer.Inner[] x", "App.Outer/Inner[]")]
    [DataRow("List<string>? x", "System.Collections.Generic.List`1<System.String>")]
    [DataRow("List<string?>? x", "System.Collections.Generic.List`1<System.String>")]
    [DataRow("Widget? x", "App.Widget")]
    [DataRow("Point? x", "System.Nullable`1<App.Point>")]
    [DataRow("Box<int>? x", "System.Nullable`1<App.Box`1<System.Int32>>")]
    [DataRow("Dictionary<string,List<Widget?>?>? x", "System.Collections.Generic.Dictionary`2<System.String,System.Collections.Generic.List`1<App.Widget>>")]
    [DataRow("_Widget2? x", "App._Widget2")]
    [DataRow("ref readonly int x", "System.Int32&")]
    [DataRow("ref readonly Point? x", "System.Nullable`1<App.Point>&")]
    public void CompleteSameArityOverloads_MatchTypesAndKeepTheirOwnComplexity(string parameter, string ilType)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("M", $"({ilType})", null, m => m.Line(1, 1)).Method("M", "(System.Boolean)", null, m => m.Line(2, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method($"void A.M({parameter})", 2, 1).Method("void A.M(bool value)", 7, 1));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.IsEmpty(report.UnmatchedMetricsMembers);
        Assert.AreSequenceEqual([2, 7], report.Methods.Select(m => m.Complexity), SequenceOrder.InAnyOrder);
        Assert.AreEqual(GateOutcome.Pass, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MultipleParameters_AndExcludedOverloads_KeepSeparateIdentities(bool exclude)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("Kept", "()", "1", m => m.Line(1, 1))).AddClass("src/A.g.cs", "App.A", c => c.Method("M", "(System.Int32,System.String)", null, m => m.Line(2, 1)).Method("M", "(System.String,System.Int32)", null, m => m.Line(3, 1))).ToBytes());
        var coverage = CoberturaParser.ParseMethods(input);
        if (exclude)
            coverage = coverage.Exclude([".g.cs"], []);
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("void A.M(int x, string y)", 2, 1).Method("void A.M(string x, int y)", 7, 1));
        var report = CrapAnalysis.Analyze(coverage, metrics);
        Assert.IsEmpty(report.UnmatchedMetricsMembers);
        Assert.AreEqual(GateOutcome.Pass, report.Evaluate(30).Outcome);
        Assert.HasCount(exclude ? 1 : 3, report.Methods);
    }

    [TestMethod]
    [DataRow("void A.M(int)", "(System.Int32)")]
    [DataRow("void A.M)", "(System.Int32)")]
    [DataRow("void A.M(One.Widget x)", "(Other.Widget)")]
    [DataRow("void A.M(int x)", "invalid)")]
    [DataRow("void A.M(int x)", "(invalid")]
    public void UnknownOrDifferentSignatures_CannotProveCompleteCoverage(string display, string signature)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("M", signature, "1", m => m.Line(1, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method(display, 2, 1).Method("void A.M(bool value)", 7, 1));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.IsNotEmpty(report.UnmatchedMetricsMembers);
        Assert.AreEqual(GateOutcome.NoData, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    public void RepeatedSameSignatureAcrossFiles_CannotReplaceAMissingOverload()
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("M", "(System.Int32)", "1", m => m.Line(1, 1))).AddClass("other/A.cs", "App.A", c => c.Method("M", "(System.Int32)", "1", m => m.Line(1, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("void A.M(int x)", 2, 1).Method("void A.M(string value)", 7, 1));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.AreSequenceEqual(["void A.M(string value)"], report.UnmatchedMetricsMembers, SequenceOrder.InAnyOrder);
        Assert.AreEqual(GateOutcome.NoData, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    [DataRow("get", false)]
    [DataRow("set", false)]
    [DataRow("init", false)]
    [DataRow("get", true)]
    [DataRow("set", true)]
    public void SameArityIndexers_MatchParametersAndSetterValue(string accessor, bool missing)
    {
        var method = accessor == "get" ? "get_Item" : "set_Item";
        var value = accessor == "get" ? "" : ",System.String";
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c =>
        {
            c.Method(method, $"(System.Int32{value})", null, m => m.Line(1, 1));
            if (!missing)
                c.Method(method, $"(System.String{value})", null, m => m.Line(2, 1));
        }).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method($"string A.this[int x].{accessor}", 2, 1).Method($"string A.this[string x].{accessor}", 7, 1));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.HasCount(missing ? 1 : 0, report.UnmatchedMetricsMembers);
        Assert.AreSequenceEqual(missing ? [2] : [2, 7], report.Methods.Select(m => m.Complexity), SequenceOrder.InAnyOrder);
        Assert.AreEqual(missing ? GateOutcome.NoData : GateOutcome.Pass, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    [DataRow("string A.this[int x.get")]
    [DataRow("string A.this[int x].unknown")]
    [DataRow("A.this[int x].set")]
    public void MalformedIndexerSignature_DoesNotProveAnOverloadMatch(string display)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("get_Item", "(System.Int32)", "1", m => m.Line(1, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method(display, 2, 1).Method("string A.this[bool x].get", 7, 1));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.IsNotEmpty(report.UnmatchedMetricsMembers);
        Assert.AreEqual(GateOutcome.NoData, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    [DataRow("<", "op_LessThan")]
    [DataRow(">", "op_GreaterThan")]
    [DataRow("<<", "op_LeftShift")]
    [DataRow(">>", "op_RightShift")]
    [DataRow(">>>", "op_UnsignedRightShift")]
    [DataRow("<=", "op_LessThanOrEqual")]
    [DataRow(">=", "op_GreaterThanOrEqual")]
    public void CompleteOperatorOverloads_IgnoreAngleTokensWhenLocatingParameters(string token, string name)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method(name, "(App.A,System.Int32)", null, m => m.Line(1, 1)).Method(name, "(App.A,System.String)", null, m => m.Line(2, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method($"bool A.operator {token}(App.A left, int right)", 2, 1).Method($"bool A.operator {token}(App.A left, string right)", 7, 1));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.IsEmpty(report.UnmatchedMetricsMembers);
        Assert.IsEmpty(report.Unscored);
        Assert.AreSequenceEqual([2, 7], report.Methods.Select(m => m.Complexity), SequenceOrder.InAnyOrder);
        Assert.AreEqual(GateOutcome.Pass, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DistinctMethods_CannotReuseOneMetricsRow_UnlessBothHaveEmbeddedMeasurements(bool embedded)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("M", "()", embedded ? "2" : null, m => m.Line(1, 1))).AddClass("other/A.cs", "App.A", c => c.Method("M", "()", embedded ? "7" : null, m => m.Line(2, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("void A.M()", 2, 1));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.HasCount(embedded ? 2 : 1, report.Methods);
        Assert.HasCount(embedded ? 0 : 1, report.Unscored);
        Assert.AreEqual(embedded ? GateOutcome.Pass : GateOutcome.NoData, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    public void PropertyFallback_CanServeBothAccessors_ButCannotCrossFileIdentity()
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("get_Value", "()", null, m => m.Line(1, 1)).Method("set_Value", "(System.Int32)", null, m => m.Line(2, 1))).AddClass("other/A.cs", "App.A", c => c.Method("get_Value", "()", null, m => m.Line(3, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Property("int A.Value", 2));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.HasCount(2, report.Methods);
        Assert.AreEqual("other/A.cs", Assert.ContainsSingle(report.Unscored).File);
        Assert.AreEqual(GateOutcome.NoData, report.Evaluate(30).Outcome);
    }
}
