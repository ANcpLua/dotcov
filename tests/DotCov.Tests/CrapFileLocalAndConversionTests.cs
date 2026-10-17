using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;
[TestClass]
public sealed class CrapFileLocalAndConversionTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FileLocalTypes_StayScopedToTheirSourceFiles(bool missing)
    {
        var first = "App.<First>F" + new string ('A', 64) + "__Worker";
        var second = "App.<Second>F" + new string ('B', 64) + "__Worker";
        var doc = Cobertura.NewDoc().AddClass("src/First.cs", first, c => c.Method("M", $"({first})", null, m => m.Line(1, 1)));
        if (!missing)
            doc.AddClass("src/Second.cs", second, c => c.Method("M", $"({second})", null, m => m.Line(2, 1)));
        using var input = new MemoryStream(doc.ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "Worker", m => m.Method("void Worker.M(Worker value)", 2, 1).Method("void Worker.M(Worker value)", 3, 1)).Select((m, i) => m with { File = i is 0 ? "src/First.cs" : "src/Second.cs" }).ToArray();
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.HasCount(missing ? 1 : 0, report.UnmatchedMetricsMembers);
        Assert.AreEqual(missing ? GateOutcome.NoData : GateOutcome.Pass, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    [DataRow("Mystery? value")]
    [DataRow("List>int<? value")]
    [DataRow("? value")]
    [DataRow("Point?? value")]
    public void UnresolvableNullableSignatures_DoNotProveCoverage(string parameter)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("M", "(System.Nullable`1<App.Point>)", "1", m => m.Line(1, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method($"void A.M({parameter})", 2, 1));
        Assert.AreEqual(GateOutcome.NoData, CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics).Evaluate(30).Outcome);
    }

    [TestMethod]
    public void MissingNullableStructOverload_CannotPassUsingNonNullableCoverage()
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("M", "(App.Point)", "1", m => m.Line(1, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("void A.M(Point value)", 2, 1).Method("void A.M(Point? value)", 3, 1));
        Assert.AreEqual(GateOutcome.NoData, CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics).Evaluate(30).Outcome);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ConversionTargetsAbsentFromCoverage_RemainAmbiguous(bool duplicateEntry)
    {
        var doc = Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("op_Implicit", "(App.A)", "2", m => m.Line(1, 1)));
        // The same shape can be a second target conversion or a repeated/partitioned
        // report of the first. Without return types, cardinality cannot distinguish them.
        if (duplicateEntry)
            doc.AddClass("src/A.cs", "App.A", c => c.Method("op_Implicit", "(App.A)", "3", m => m.Line(2, 1)));
        using var input = new MemoryStream(doc.ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("A.implicit operator int(A value)", 2, 1).Method("A.implicit operator string(A value)", 3, 1));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.HasCount(2, report.UnmatchedMetricsMembers);
        Assert.AreEqual(GateOutcome.NoData, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    public void EscapedIdentifierTypes_MatchTheirMetadataIdentity()
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("M", "(App.int)", null, m => m.Line(1, 1)).Method("M", "(System.Int32)", null, m => m.Line(2, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("void A.M(@int value)", 2, 1).Method("void A.M(int value)", 7, 1));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.IsEmpty(report.UnmatchedMetricsMembers);
        Assert.AreSequenceEqual([2, 7], report.Methods.Select(m => m.Complexity), SequenceOrder.InAnyOrder);
        Assert.AreEqual(GateOutcome.Pass, report.Evaluate(30).Outcome);
    }

    // Coverlet cuts the signature at the last '(' of the IL name, so a function-pointer
    // parameter arrives as "(System.Int32))"; Cecil's full spelling is the second shape.
    [TestMethod]
    [DataRow("(System.Int32))", "delegate*<int, void> callback")]
    [DataRow("(method System.Void *(System.Int32))", "delegate* unmanaged[Cdecl]<int, void> callback")]
    public void FunctionPointerParameters_AreUnknownSignatures_NotConflicts(string signature, string parameter)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("M", signature, null, m => m.Line(1, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method($"void A.M({parameter})", 2, 1));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.IsEmpty(report.UnmatchedMetricsMembers);
        Assert.AreEqual(2, Assert.ContainsSingle(report.Methods).Complexity);
        Assert.AreEqual(GateOutcome.Pass, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    public void NullableGenericParameters_ResolvePerMethod_NotAcrossTheReport()
    {
        // A<T>(T? value) where T : class and B<T>(T? value) where T : struct share the name T.
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("A", "(T)", null, m => m.Line(1, 1)).Method("B", "(System.Nullable`1<T>)", null, m => m.Line(2, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("void A.A<T>(T? value)", 2, 1).Method("void A.B<T>(T? value)", 7, 1));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.IsEmpty(report.UnmatchedMetricsMembers);
        Assert.AreSequenceEqual([2, 7], report.Methods.Select(m => m.Complexity), SequenceOrder.InAnyOrder);
        Assert.AreEqual(GateOutcome.Pass, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    public void FunctionPointerMetrics_DoNotMatchAMethodWithAnotherSignature()
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("M", "(System.String)", null, m => m.Line(1, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("void A.M(delegate*<int, void> callback)", 2, 1));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.AreSequenceEqual(["void A.M(delegate*<int, void> callback)"], report.UnmatchedMetricsMembers, SequenceOrder.InAnyOrder);
        Assert.AreEqual(GateOutcome.NoData, report.Evaluate(30).Outcome);
    }

    // Coverlet often emits only the state machines of async overloads, which carry no signature.
    // Bodies and rows pair only when their counts agree.
    [TestMethod]
    [DataRow("complete")]
    [DataRow("missing body")]
    [DataRow("extra row")]
    public void AsyncOverloadsMeasuredOnlyThroughStateMachines_MatchTheirMetricsRowsTogether(string shape)
    {
        var doc = Cobertura.NewDoc().AddClass("src/A.cs", "App.A/<M>d__0", c => c.Method("MoveNext", "()", null, m => m.Line(1, 1)));
        if (shape is not "missing body")
            doc.AddClass("src/A.cs", "App.A/<M>d__1", c => c.Method("MoveNext", "()", null, m => m.Line(5, 1)));
        using var input = new MemoryStream(doc.ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m =>
        {
            m.Method("Task A.M(int value)", 2, 1).Method("Task A.M(string value)", 3, 1);
            if (shape is "extra row")
                m.Method("Task A.M(bool value)", 1, 1);
        });
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        // Which body is which overload is unknown: every body carries the largest complexity.
        Assert.HasCount(shape is "missing body" ? 1 : 2, report.Methods);
        Assert.DoesNotContain(static m => m.Complexity != 3, report.Methods);
        Assert.HasCount(shape switch
        {
            "complete" => 0,
            "missing body" => 2,
            _ => 3
        }, report.UnmatchedMetricsMembers);
        Assert.AreEqual(shape is "complete" ? GateOutcome.Pass : GateOutcome.NoData, report.Evaluate(30).Outcome);
    }

    // Rows without a source file cannot say which of two files' bodies they measured.
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AsyncOverloadBodiesInTwoFiles_NeedRowsNamingTheirFile(bool rowsNameTheirFile)
    {
        var doc = Cobertura.NewDoc();
        foreach (var file in new[]
        {
            "src/A.cs",
            "src/A.Part.cs"
        }

        )
            doc.AddClass(file, "App.A/<M>d__0", c => c.Method("MoveNext", "()", null, m => m.Line(1, 1))).AddClass(file, "App.A/<M>d__1", c => c.Method("MoveNext", "()", null, m => m.Line(5, 1)));
        using var input = new MemoryStream(doc.ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("Task A.M(int value)", 2, 1).Method("Task A.M(string value)", 3, 1).Method("Task A.M(long value)", 2, 1).Method("Task A.M(bool value)", 3, 1)).Select((m, i) => rowsNameTheirFile ? m with { File = i < 2 ? "src/A.cs" : "src/A.Part.cs" } : m).Take(rowsNameTheirFile ? 4 : 2).ToArray();
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.HasCount(rowsNameTheirFile ? 0 : 2, report.UnmatchedMetricsMembers);
        Assert.AreEqual(rowsNameTheirFile ? GateOutcome.Pass : GateOutcome.NoData, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    public void ChosenSourceRoot_AlsoRootsRelativeMetricsPaths()
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
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("void A.M()", 2, 1)).Select(static m => m with { File = "src/A.cs" }).ToArray();
        var report = CrapAnalysis.Analyze(coverage, metrics);
        Assert.IsEmpty(report.UnmatchedMetricsMembers);
        Assert.AreEqual(GateOutcome.Pass, report.Evaluate(30).Outcome);
    }
}
