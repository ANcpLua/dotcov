using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

public sealed class CrapSignatureMatchingTests
{
    [Test]
    [Arguments("int x", "System.Int32")]
    [Arguments("dynamic x", "System.Object")]
    [Arguments("int? x", "System.Nullable`1<System.Int32>")]
    [Arguments("decimal?[] x", "System.Nullable`1<System.Decimal>[]")]
    [Arguments("ref long? x", "System.Nullable`1<System.Int64>&")]
    [Arguments("List<int?> x", "System.Collections.Generic.List`1<System.Nullable`1<System.Int32>>")]
    [Arguments("int x = 1", "System.Int32")]
    [Arguments("char x = ','", "System.Char")]
    [Arguments("string x = \"a,(b)=c\"", "System.String")]
    [Arguments("List<int> x", "System.Collections.Generic.List`1<System.Int32>")]
    [Arguments("(int,string) x", "System.ValueTuple`2<System.Int32,System.String>")]
    [Arguments("int[,] x", "System.Int32[,]")]
    [Arguments("params int[] x", "System.Int32[]")]
    [Arguments("ref int x", "System.Int32&")]
    [Arguments("out int x", "System.Int32&")]
    [Arguments("in int x", "System.Int32&")]
    [Arguments("global::App.Widget x", "App.Widget")]
    [Arguments("Widget x", "App.Widget")]
    [Arguments("string? x", "System.String")]
    [Arguments("object? x", "System.Object")]
    [Arguments("dynamic? x", "System.Object")]
    [Arguments("string?[]? x", "System.String[]")]
    [Arguments("int?[]? x", "System.Nullable`1<System.Int32>[]")]
    [Arguments("List<string?> x", "System.Collections.Generic.List`1<System.String>")]
    [Arguments("Widget[]? x", "App.Widget[]")]
    [Arguments("Inner x", "App.Outer/Inner")]
    [Arguments("Outer.Inner x", "App.Outer/Inner")]
    [Arguments("App.Outer.Inner x", "App.Outer+Inner")]
    [Arguments("Outer.Inner[] x", "App.Outer/Inner[]")]
    public async Task CompleteSameArityOverloads_MatchTypesAndKeepTheirOwnComplexity(
        string parameter, string ilType)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c
            .Method("M", $"({ilType})", null, m => m.Line(1, 1))
            .Method("M", "(System.Boolean)", null, m => m.Line(2, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m
            .Method($"void A.M({parameter})", 2, 1).Method("void A.M(bool value)", 7, 1));

        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);

        await Assert.That(report.UnmatchedMetricsMembers).IsEmpty();
        await Assert.That(report.Methods.Select(m => m.Complexity)).IsEquivalentTo([2, 7]);
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.Pass);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task MultipleParameters_AndExcludedOverloads_KeepSeparateIdentities(bool exclude)
    {
        using var input = new MemoryStream(Cobertura.NewDoc()
            .AddClass("src/A.cs", "App.A", c => c.Method("Kept", "()", "1", m => m.Line(1, 1)))
            .AddClass("src/A.g.cs", "App.A", c => c
                .Method("M", "(System.Int32,System.String)", null, m => m.Line(2, 1))
                .Method("M", "(System.String,System.Int32)", null, m => m.Line(3, 1))).ToBytes());
        var coverage = CoberturaParser.ParseMethods(input);
        if (exclude) coverage = coverage.Exclude([".g.cs"], []);
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m
            .Method("void A.M(int x, string y)", 2, 1).Method("void A.M(string x, int y)", 7, 1));
        var report = CrapAnalysis.Analyze(coverage, metrics);
        await Assert.That(report.UnmatchedMetricsMembers).IsEmpty();
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.Pass);
        await Assert.That(report.Methods.Count).IsEqualTo(exclude ? 1 : 3);
    }

    [Test]
    [Arguments("void A.M(int)", "(System.Int32)")]
    [Arguments("void A.M)", "(System.Int32)")]
    [Arguments("void A.M(One.Widget x)", "(Other.Widget)")]
    [Arguments("void A.M(int x)", "invalid)")]
    [Arguments("void A.M(int x)", "(invalid")]
    public async Task UnknownOrDifferentSignatures_CannotProveCompleteCoverage(string display, string signature)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c
            .Method("M", signature, "1", m => m.Line(1, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m
            .Method(display, 2, 1).Method("void A.M(bool value)", 7, 1));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        await Assert.That(report.UnmatchedMetricsMembers).IsNotEmpty();
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.NoData);
    }

    [Test]
    public async Task RepeatedSameSignatureAcrossFiles_CannotReplaceAMissingOverload()
    {
        using var input = new MemoryStream(Cobertura.NewDoc()
            .AddClass("src/A.cs", "App.A", c => c.Method("M", "(System.Int32)", "1", m => m.Line(1, 1)))
            .AddClass("other/A.cs", "App.A", c => c.Method("M", "(System.Int32)", "1", m => m.Line(1, 1)))
            .ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m
            .Method("void A.M(int x)", 2, 1).Method("void A.M(string value)", 7, 1));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        await Assert.That(report.UnmatchedMetricsMembers).IsEquivalentTo(["void A.M(string value)"]);
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.NoData);
    }
    [Test]
    [Arguments("get", false)]
    [Arguments("set", false)]
    [Arguments("init", false)]
    [Arguments("get", true)]
    [Arguments("set", true)]
    public async Task SameArityIndexers_MatchParametersAndSetterValue(string accessor, bool missing)
    {
        var method = accessor == "get" ? "get_Item" : "set_Item";
        var value = accessor == "get" ? "" : ",System.String";
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c =>
        {
            c.Method(method, $"(System.Int32{value})", null, m => m.Line(1, 1));
            if (!missing) c.Method(method, $"(System.String{value})", null, m => m.Line(2, 1));
        }).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m
            .Method($"string A.this[int x].{accessor}", 2, 1)
            .Method($"string A.this[string x].{accessor}", 7, 1));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        await Assert.That(report.UnmatchedMetricsMembers.Count).IsEqualTo(missing ? 1 : 0);
        await Assert.That(report.Methods.Select(m => m.Complexity)).IsEquivalentTo(missing ? [2] : new[] { 2, 7 });
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(missing ? GateOutcome.NoData : GateOutcome.Pass);
    }

    [Test]
    [Arguments("string A.this[int x.get")]
    [Arguments("string A.this[int x].unknown")]
    [Arguments("A.this[int x].set")]
    public async Task MalformedIndexerSignature_DoesNotProveAnOverloadMatch(string display)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c =>
            c.Method("get_Item", "(System.Int32)", "1", m => m.Line(1, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m
            .Method(display, 2, 1).Method("string A.this[bool x].get", 7, 1));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        await Assert.That(report.UnmatchedMetricsMembers).IsNotEmpty();
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.NoData);
    }

    [Test]
    [Arguments("<", "op_LessThan")]
    [Arguments(">", "op_GreaterThan")]
    [Arguments("<<", "op_LeftShift")]
    [Arguments(">>", "op_RightShift")]
    [Arguments(">>>", "op_UnsignedRightShift")]
    [Arguments("<=", "op_LessThanOrEqual")]
    [Arguments(">=", "op_GreaterThanOrEqual")]
    public async Task CompleteOperatorOverloads_IgnoreAngleTokensWhenLocatingParameters(string token, string name)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c
            .Method(name, "(App.A,System.Int32)", null, m => m.Line(1, 1))
            .Method(name, "(App.A,System.String)", null, m => m.Line(2, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m
            .Method($"bool A.operator {token}(App.A left, int right)", 2, 1)
            .Method($"bool A.operator {token}(App.A left, string right)", 7, 1));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        await Assert.That(report.UnmatchedMetricsMembers).IsEmpty();
        await Assert.That(report.Unscored).IsEmpty();
        await Assert.That(report.Methods.Select(m => m.Complexity)).IsEquivalentTo([2, 7]);
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.Pass);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DistinctMethods_CannotReuseOneMetricsRow_UnlessBothHaveEmbeddedMeasurements(bool embedded)
    {
        using var input = new MemoryStream(Cobertura.NewDoc()
            .AddClass("src/A.cs", "App.A", c => c.Method("M", "()", embedded ? "2" : null, m => m.Line(1, 1)))
            .AddClass("other/A.cs", "App.A", c => c.Method("M", "()", embedded ? "7" : null, m => m.Line(2, 1)))
            .ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("void A.M()", 2, 1));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        await Assert.That(report.Methods.Count).IsEqualTo(embedded ? 2 : 1);
        await Assert.That(report.Unscored.Count).IsEqualTo(embedded ? 0 : 1);
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(embedded ? GateOutcome.Pass : GateOutcome.NoData);
    }

    [Test]
    public async Task PropertyFallback_CanServeBothAccessors_ButCannotCrossFileIdentity()
    {
        using var input = new MemoryStream(Cobertura.NewDoc()
            .AddClass("src/A.cs", "App.A", c => c
                .Method("get_Value", "()", null, m => m.Line(1, 1))
                .Method("set_Value", "(System.Int32)", null, m => m.Line(2, 1)))
            .AddClass("other/A.cs", "App.A", c => c.Method("get_Value", "()", null, m => m.Line(3, 1)))
            .ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Property("int A.Value", 2));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        await Assert.That(report.Methods.Count).IsEqualTo(2);
        await Assert.That(report.Unscored.Single().File).IsEqualTo("other/A.cs");
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.NoData);
    }

}
