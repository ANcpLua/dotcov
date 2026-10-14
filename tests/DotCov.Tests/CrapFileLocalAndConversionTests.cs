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

    [Test]
    public async Task FunctionPointerMetrics_DoNotMatchAMethodWithAnotherSignature()
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c
            .Method("M", "(System.String)", null, m => m.Line(1, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("void A.M(delegate*<int, void> callback)", 2, 1));

        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);

        await Assert.That(report.UnmatchedMetricsMembers).IsEquivalentTo(["void A.M(delegate*<int, void> callback)"]);
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.NoData);
    }

    // Coverlet often emits only the state machines of async overloads, which carry no signature.
    // Bodies and rows pair only when their counts agree.
    [Test]
    [Arguments("complete")]
    [Arguments("missing body")]
    [Arguments("extra row")]
    public async Task AsyncOverloadsMeasuredOnlyThroughStateMachines_MatchTheirMetricsRowsTogether(string shape)
    {
        var doc = Cobertura.NewDoc()
            .AddClass("src/A.cs", "App.A/<M>d__0", c => c.Method("MoveNext", "()", null, m => m.Line(1, 1)));
        if (shape is not "missing body")
            doc.AddClass("src/A.cs", "App.A/<M>d__1", c => c.Method("MoveNext", "()", null, m => m.Line(5, 1)));
        using var input = new MemoryStream(doc.ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m =>
        {
            m.Method("Task A.M(int value)", 2, 1).Method("Task A.M(string value)", 3, 1);
            if (shape is "extra row") m.Method("Task A.M(bool value)", 1, 1);
        });

        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);

        // Which body is which overload is unknown: every body carries the largest complexity.
        await Assert.That(report.Methods.Select(m => m.Complexity)).All(static c => c == 3);
        await Assert.That(report.UnmatchedMetricsMembers.Count).IsEqualTo(shape switch
        {
            "complete" => 0,
            "missing body" => 2,
            _ => 3
        });
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(shape is "complete" ? GateOutcome.Pass : GateOutcome.NoData);
    }

    [Test]
    public async Task ChosenSourceRoot_AlsoRootsRelativeMetricsPaths()
    {
        var xml = """
            <coverage><sources><source>/other</source><source>/repo</source></sources>
              <packages><package name="App"><classes>
                <class name="App.A" filename="src/A.cs"><methods>
                  <method name="M" signature="()"><lines><line number="1" hits="1" /></lines></method>
                </methods><lines><line number="1" hits="1" /></lines></class>
              </classes></package></packages></coverage>
            """;
        using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(xml));
        var coverage = CoberturaParser.ParseMethods(input, identity: FileIdentity.FromSourceRoot("/repo"));
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("void A.M()", 2, 1))
            .Select(static m => m with { File = "src/A.cs" }).ToArray();

        var report = CrapAnalysis.Analyze(coverage, metrics);

        await Assert.That(report.UnmatchedMetricsMembers).IsEmpty();
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.Pass);
    }
}
