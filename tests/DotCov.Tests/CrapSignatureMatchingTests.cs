using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

public sealed class CrapSignatureMatchingTests
{
    [Test]
    [Arguments("int x", "System.Int32")]
    [Arguments("int x = 1", "System.Int32")]
    [Arguments("char x = ','", "System.Char")]
    [Arguments("string x = \"a,(b)=c\"", "System.String")]
    [Arguments("List<int> x", "List<System.Int32>")]
    [Arguments("(int,string) x", "(System.Int32,System.String)")]
    [Arguments("int[,] x", "System.Int32[,]")]
    [Arguments("params int[] x", "System.Int32[]")]
    [Arguments("ref int x", "System.Int32&")]
    [Arguments("out int x", "System.Int32&")]
    [Arguments("in int x", "System.Int32&")]
    [Arguments("global::App.Widget x", "App.Widget")]
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
}
