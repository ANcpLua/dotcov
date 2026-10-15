using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;
[TestClass]
public sealed class CrapTupleAndRootTests
{
    [TestMethod]
    [DataRow("(int x, string y)", "System.ValueTuple`2<System.Int32,System.String>")]
    [DataRow("( int , string )", "System.ValueTuple`2<System.Int32,System.String>")]
    [DataRow("(int @number, string snake_case)", "System.ValueTuple`2<System.Int32,System.String>")]
    [DataRow("(App.Widget, string)", "System.ValueTuple`2<App.Widget,System.String>")]
    [DataRow("((int a,string b) inner,bool ok)", "System.ValueTuple`2<System.ValueTuple`2<System.Int32,System.String>,System.Boolean>")]
    [DataRow("(int[,] grid,List<string> names)[]", "System.ValueTuple`2<System.Int32[,],System.Collections.Generic.List`1<System.String>>[]")]
    [DataRow("(int x,string y)?", "System.Nullable`1<System.ValueTuple`2<System.Int32,System.String>>")]
    [DataRow("Dictionary<string,(int,string)>", "System.Collections.Generic.Dictionary`2<System.String,System.ValueTuple`2<System.Int32,System.String>>")]
    [DataRow("(int,int,int,int,int,int,int,int)", "System.ValueTuple`8<System.Int32,System.Int32,System.Int32,System.Int32,System.Int32,System.Int32,System.Int32,System.ValueTuple`1<System.Int32>>")]
    public void TupleParameters_MatchRuntimeMetadata(string source, string runtime)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("M", $"({runtime})", null, m => m.Line(1, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method($"void A.M({source} value)", 2, 1));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.IsEmpty(report.UnmatchedMetricsMembers);
        Assert.AreEqual(GateOutcome.Pass, report.Evaluate(30).Outcome);
    }

    // Cobertura carries IL names, where tuples are ValueTuple`N: parentheses there come from a
    // truncated function pointer, so the signature is unknown and the method still analyzed.
    [TestMethod]
    [DataRow("left")]
    [DataRow("right")]
    [DataRow("deep")]
    [DataRow("empty")]
    public void ParenthesesInCoverageSignatures_AreUnknown_NotErrors(string shape)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("M", $"({TupleShape(shape)})", "1", m => m.Line(1, 1))).ToBytes());
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input));
        Assert.AreEqual(1, report.Methods.Single().Complexity);
    }

    // Roslyn metrics displays do spell tuples in source syntax: malformed or too deep is an input error.
    [TestMethod]
    [DataRow("left")]
    [DataRow("right")]
    [DataRow("deep")]
    [DataRow("empty")]
    public void InvalidTupleStructure_InMetrics_IsRejected(string shape)
    {
        var display = $"void A.M({TupleShape(shape)} x)";
        var error = Assert.Throws<InvalidDataException>(() => CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method(display, 2, 1)));
        Assert.StartsWith($"Metrics member display '{display[..Math.Min(display.Length, 200)]}': ", error.Message);
    }

    // A display parses when its brackets balance as a whole, so the stray tuple parenthesis
    // pairs with an angle bracket: "(int>" opens a tuple that never closes.
    private static string TupleShape(string shape) => shape switch
    {
        "left" => "(int>",
        "right" => "<int)",
        "empty" => "( , int)",
        _ => new string ('(', 65) + "int" + string.Concat(Enumerable.Repeat(",int)", 65))};
    [TestMethod]
    public void TupleDepthBoundary_RemainsMeasurable()
    {
        var source = "int";
        var runtime = "System.Int32";
        for (var i = 0; i < 64; i++)
        {
            source = $"({source},int)";
            runtime = $"System.ValueTuple`2<{runtime},System.Int32>";
        }

        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("M", $"({runtime})", null, m => m.Line(1, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method($"void A.M({source} value)", 2, 1));
        Assert.AreEqual(GateOutcome.Pass, CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics).Evaluate(30).Outcome);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void RootedExclusion_AppliesToRelativeMetricsOnlyFiles(bool keep)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().WithSource("/repo").AddClass("src/Kept.cs", "App.Kept", c => c.Method("M", "()", "1", m => m.Line(1, 1))).ToBytes());
        var report = CoberturaParser.ParseMethods(input).Exclude(["/repo/src/Generated.g.cs"], keep ? ["/repo/src/Generated.g.cs"] : []);
        var metrics = CodeMetricsReportBuilder.ParseType("App", "Generated", m => m.Method("void Generated.M()", 2, 1)).Select(m => m with { File = "src/Generated.g.cs" }).ToArray();
        var analysis = CrapAnalysis.Analyze(report, metrics);
        Assert.AreEqual(keep ? 1 : 0, analysis.UnmatchedMetricsMembers.Count);
        Assert.AreEqual(keep ? GateOutcome.NoData : GateOutcome.Pass, analysis.Evaluate(30).Outcome);
    }

    [TestMethod]
    public void AmbiguousCustomTypeNames_StayUnresolved()
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c.Method("M", "(One.Widget)", null, m => m.Line(1, 1)).Method("M", "(Two.Widget)", null, m => m.Line(2, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("void A.M(Widget value)", 2, 1).Method("void A.M(Two.Widget value)", 3, 1));
        Assert.AreEqual(GateOutcome.NoData, CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics).Evaluate(30).Outcome);
    }
}
