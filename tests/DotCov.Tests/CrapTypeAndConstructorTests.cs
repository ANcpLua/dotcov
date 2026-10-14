using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

public sealed class CrapTypeAndConstructorTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GenericAndNonGenericTypes_KeepSeparateMetrics(bool exclude)
    {
        using var input = new MemoryStream(Cobertura.NewDoc()
            .AddClass("src/Kept.cs", "App.Kept", c => c.Method("M", "()", "1", m => m.Line(1, 1)))
            .AddClass("src/A.g.cs", "App.Result", c => c.Method("M", "()", null, m => m.Line(2, 1)))
            .AddClass("src/A.g.cs", "App.Result`1", c => c.Method("M", "()", null, m => m.Line(3, 1)))
            .AddClass("src/A.g.cs", "App.Outer`1/Inner`2", c => c.Method("M", "()", null, m => m.Line(4, 1)))
            .ToBytes());
        var metrics = CodeMetricsReportBuilder.Create().Namespace("App", types => types
            .Type("Result", m => m.Method("void Result.M()", 2, 1))
            .Type("Result<T>", m => m.Method("void Result<T>.M()", 7, 1))
            .Type("Outer<T>.Inner<U, V>", m => m.Method("void Inner<U, V>.M()", 9, 1))).Parse();
        var coverage = CoberturaParser.ParseMethods(input);
        if (exclude) coverage = coverage.Exclude([".g.cs"], []);
        var report = CrapAnalysis.Analyze(coverage, metrics);
        await Assert.That(report.UnmatchedMetricsMembers).IsEmpty();
        await Assert.That(report.Unscored).IsEmpty();
        await Assert.That(report.Methods.Select(m => m.Complexity)).IsEquivalentTo(exclude ? [1] : new[] { 1, 2, 7, 9 });
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.Pass);
        await Assert.That(metrics[1].TypeName).IsEqualTo("App.Result");
        await Assert.That(metrics[1].TypeIdentity).IsEqualTo("App.Result`1");
    }

    [Test]
    [Arguments(null)]
    [Arguments("UNKNOWN")]
    [Arguments("src/./A.cs")]
    public async Task CompleteConstructorPair_IsMatchedWithConservativeComplexity(string? source)
    {
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m
            .Method("A.A()", 2, 1).Method("A.A()", 7, 1).Method("A.A(int x)", 3, 1))
            .Select(m => m with { File = source }).ToArray();
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c
            .Method(".ctor", "()", null, m => m.Line(1, 1))
            .Method(".cctor", "()", null, m => m.Line(2, 1))
            .Method(".ctor", "(System.Int32)", null, m => m.Line(3, 1))).ToBytes());
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        await Assert.That(report.UnmatchedMetricsMembers).IsEmpty();
        await Assert.That(report.Methods.Select(m => m.Complexity)).IsEquivalentTo([7, 7, 3]);
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.Pass);
    }

    [Test]
    [Arguments(null, false)]
    [Arguments(null, true)]
    [Arguments("UNKNOWN", false)]
    [Arguments("UNKNOWN", true)]
    [Arguments("src/A.cs", false)]
    [Arguments("src/A.cs", true)]
    public async Task ConstructorPair_CannotBeReusedAcrossFiles(string? source, bool secondPair)
    {
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m
            .Method("A.A()", 2, 1).Method("A.A()", 7, 1))
            .Select(m => m with { File = source }).ToArray();
        var doc = Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c
            .Method(".ctor", "()", null, m => m.Line(1, 1))
            .Method(".cctor", "()", null, m => m.Line(2, 1)))
            .AddClass("other/A.cs", "App.A", c =>
            {
                c.Method(".ctor", "()", null, m => m.Line(3, 1));
                if (secondPair) c.Method(".cctor", "()", null, m => m.Line(4, 1));
            });
        using var input = new MemoryStream(doc.ToBytes());

        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);

        await Assert.That(report.UnmatchedMetricsMembers.Count).IsEqualTo(2);
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.NoData);
    }

    [Test]
    [Arguments("missing")]
    [Arguments("no-lines")]
    [Arguments("other-file")]
    [Arguments("duplicate-instance")]
    [Arguments("wrong-source")]
    [Arguments("extra-metrics")]
    [Arguments("unknown-signature")]
    public async Task IncompleteOrAmbiguousConstructorPair_CannotClearMetrics(string scenario)
    {
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m =>
        {
            m.Method("A.A()", 2, 1).Method(scenario == "unknown-signature" ? "A.A" : "A.A()", 7, 1);
            if (scenario == "extra-metrics") m.Method("A.A()", 4, 1);
        }).Select(m => m with { File = scenario == "wrong-source" ? "other/A.cs" : null }).ToArray();
        var doc = Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c
            .Method(".ctor", "()", "1", m => m.Line(1, 1)));
        if (scenario != "missing")
            doc.AddClass(scenario is "other-file" or "duplicate-instance" ? "other/A.cs" : "src/A.cs", "App.A", c => c
                .Method(scenario == "duplicate-instance" ? ".ctor" : ".cctor", "()", "1", m =>
                {
                    if (scenario != "no-lines") m.Line(2, 1);
                }));
        using var input = new MemoryStream(doc.ToBytes());
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        await Assert.That(report.UnmatchedMetricsMembers).IsNotEmpty();
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.NoData);
    }
    [Test]
    [Arguments("Ext.extension<T>(T)", "App.Ext")]
    [Arguments("Outer<T>.Ext.extension<U>(U)", "App.Outer`1.Ext")]
    public async Task GenericExtensionBlock_StaticMemberBelongsToItsEnclosingType(string owner, string identity)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/Ext.cs", identity, c => c
            .Method("Make", "()", null, m => m.Line(1, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", owner, m => m
            .Method("int extension<T>(T).Make()", 2, 1));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        await Assert.That(metrics.Single().TypeIdentity).IsEqualTo(identity);
        await Assert.That(report.UnmatchedMetricsMembers).IsEmpty();
        await Assert.That(report.Unscored).IsEmpty();
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.Pass);
    }

}
