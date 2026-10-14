using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

public sealed class CrapFileLocalAndConversionTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task FileLocalTypes_StayScopedToTheirSourceFiles(bool missing)
    {
        var first = "App.<First>F" + new string('A', 64) + "__Worker";
        var second = "App.<Second>F" + new string('B', 64) + "__Worker";
        var doc = Cobertura.NewDoc().AddClass("src/First.cs", first, c => c
            .Method("M", $"({first})", null, m => m.Line(1, 1)));
        if (!missing) doc.AddClass("src/Second.cs", second, c => c
            .Method("M", $"({second})", null, m => m.Line(2, 1)));
        using var input = new MemoryStream(doc.ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "Worker", m => m
            .Method("void Worker.M(Worker value)", 2, 1).Method("void Worker.M(Worker value)", 3, 1))
            .Select((m, i) => m with { File = i is 0 ? "src/First.cs" : "src/Second.cs" }).ToArray();
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        await Assert.That(report.UnmatchedMetricsMembers.Count).IsEqualTo(missing ? 1 : 0);
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(missing ? GateOutcome.NoData : GateOutcome.Pass);
    }

    [Test]
    [Arguments("Mystery? value")]
    [Arguments("List>int<? value")]
    [Arguments("? value")]
    [Arguments("Point?? value")]
    public async Task UnresolvableNullableSignatures_DoNotProveCoverage(string parameter)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c
            .Method("M", "(System.Nullable`1<App.Point>)", "1", m => m.Line(1, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method($"void A.M({parameter})", 2, 1));
        await Assert.That(CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics).Evaluate(30).Outcome)
            .IsEqualTo(GateOutcome.NoData);
    }

    [Test]
    public async Task MissingNullableStructOverload_CannotPassUsingNonNullableCoverage()
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c
            .Method("M", "(App.Point)", "1", m => m.Line(1, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m
            .Method("void A.M(Point value)", 2, 1).Method("void A.M(Point? value)", 3, 1));
        await Assert.That(CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics).Evaluate(30).Outcome)
            .IsEqualTo(GateOutcome.NoData);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConversionTargetsAbsentFromCoverage_RemainAmbiguous(bool duplicateEntry)
    {
        var doc = Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c
            .Method("op_Implicit", "(App.A)", "2", m => m.Line(1, 1)));
        // The same shape can be a second target conversion or a repeated/partitioned
        // report of the first. Without return types, cardinality cannot distinguish them.
        if (duplicateEntry) doc.AddClass("src/A.cs", "App.A", c => c
            .Method("op_Implicit", "(App.A)", "3", m => m.Line(2, 1)));
        using var input = new MemoryStream(doc.ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m
            .Method("A.implicit operator int(A value)", 2, 1)
            .Method("A.implicit operator string(A value)", 3, 1));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        await Assert.That(report.UnmatchedMetricsMembers.Count).IsEqualTo(2);
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.NoData);
    }

    [Test]
    public async Task EscapedIdentifierTypes_MatchTheirMetadataIdentity()
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c
            .Method("M", "(App.int)", null, m => m.Line(1, 1))
            .Method("M", "(System.Int32)", null, m => m.Line(2, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m
            .Method("void A.M(@int value)", 2, 1).Method("void A.M(int value)", 7, 1));

        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);

        await Assert.That(report.UnmatchedMetricsMembers).IsEmpty();
        await Assert.That(report.Methods.Select(m => m.Complexity)).IsEquivalentTo([2, 7]);
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.Pass);
    }

    // Coverlet cuts the signature at the last '(' of the IL name, so a function-pointer
    // parameter arrives as "(System.Int32))"; Cecil's full spelling is the second shape.
    [Test]
    [Arguments("(System.Int32))", "delegate*<int, void> callback")]
    [Arguments("(method System.Void *(System.Int32))", "delegate* unmanaged[Cdecl]<int, void> callback")]
    public async Task FunctionPointerParameters_AreUnknownSignatures_NotConflicts(string signature, string parameter)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c
            .Method("M", signature, null, m => m.Line(1, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method($"void A.M({parameter})", 2, 1));

        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);

        await Assert.That(report.UnmatchedMetricsMembers).IsEmpty();
        await Assert.That(report.Methods.Single().Complexity).IsEqualTo(2);
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.Pass);
    }

    [Test]
    public async Task NullableGenericParameters_ResolvePerMethod_NotAcrossTheReport()
    {
        // A<T>(T? value) where T : class and B<T>(T? value) where T : struct share the name T.
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c
            .Method("A", "(T)", null, m => m.Line(1, 1))
            .Method("B", "(System.Nullable`1<T>)", null, m => m.Line(2, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m
            .Method("void A.A<T>(T? value)", 2, 1).Method("void A.B<T>(T? value)", 7, 1));

        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);

        await Assert.That(report.UnmatchedMetricsMembers).IsEmpty();
        await Assert.That(report.Methods.Select(m => m.Complexity)).IsEquivalentTo([2, 7]);
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.Pass);
    }
}
