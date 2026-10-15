using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;
/// <summary>
/// Roslyn writes Visual Basic members as <c>Sub A.M(value As Integer)</c> and
/// <c>Function A.M(value As Integer) As Long</c>; they match Coverlet's IL signatures like C# displays.
/// </summary>
[TestClass]
public sealed class CrapVisualBasicMetricsTests
{
    [TestMethod]
    [DataRow("Sub A.M(value As Integer)", "System.Int32")]
    [DataRow("Sub A.M(ByRef value As Integer)", "System.Int32&")]
    [DataRow("Sub A.M(Optional value As Integer = 5)", "System.Int32")]
    [DataRow("Sub A.M([value As Integer = 5])", "System.Int32")]
    [DataRow("Sub A.M(when As Date)", "System.DateTime")]
    [DataRow("Sub A.M(values As Integer())", "System.Int32[]")]
    [DataRow("Sub A.M(grid As Integer(,))", "System.Int32[,]")]
    [DataRow("Sub A.M(ParamArray values As String())", "System.String[]")]
    [DataRow("Sub A.M(values As List(Of Integer))", "System.Collections.Generic.List`1<System.Int32>")]
    [DataRow("Sub A.M(map As Dictionary(Of String, List(Of Long)))", "System.Collections.Generic.Dictionary`2<System.String,System.Collections.Generic.List`1<System.Int64>>")]
    [DataRow("Function A.M(value As Integer) As List(Of Long)", "System.Int32")]
    public void VisualBasicOverloads_MatchTheirOwnRows(string display, string ilType)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.vb", "App.A", c => c.Method("M", $"({ilType})", null, m => m.Line(1, 1)).Method("M", "(System.Boolean)", null, m => m.Line(2, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method(display, 2, 1).Method("Sub A.M(flag As Boolean)", 7, 1));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.IsEmpty(report.UnmatchedMetricsMembers);
        Assert.AreSequenceEqual([2, 7], report.Methods.Select(m => m.Complexity), SequenceOrder.InAnyOrder);
        Assert.AreEqual(GateOutcome.Pass, report.Evaluate(30).Outcome);
    }

    // A tuple's parenthesis opens the type or follows a space, and a function display may lack its
    // return clause: the signature stays unknown and the sole method matches by name.
    [TestMethod]
    [DataRow("Sub A.M(pair As (Integer, String))")]
    [DataRow("Sub A.M(items As List(Of (Integer, String)))")]
    [DataRow("Function A.M")]
    public void UnknownVisualBasicShapes_MatchASoleMethod(string display)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.vb", "App.A", c => c.Method("M", "(System.ValueTuple`2<System.Int32,System.String>)", null, m => m.Line(1, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method(display, 2, 1));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.IsEmpty(report.UnmatchedMetricsMembers);
        Assert.AreEqual(GateOutcome.Pass, report.Evaluate(30).Outcome);
    }

    [TestMethod]
    public void UnbalancedVisualBasicType_IsAnInputErrorNamingTheMember()
    {
        var error = Assert.Throws<InvalidDataException>(() => CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("Sub A.M(x As <Integer))", 2, 1)));
        Assert.StartsWith("Metrics member display 'Sub A.M(x As <Integer))': ", error.Message);
    }

    [TestMethod]
    [DataRow("Sub A.M(Of T)(value As T)", "M", "(T)")]
    [DataRow("Function A.M(Of T)(value As T) As T", "M", "(T)")]
    [DataRow("Sub A.New(value As Integer)", ".ctor", "(System.Int32)")]
    public void VisualBasicGenericMethodsAndConstructors_MatchTheirMethod(string display, string method, string signature)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.vb", "App.A", c => c.Method(method, signature, null, m => m.Line(1, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method(display, 2, 1));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        Assert.IsEmpty(report.UnmatchedMetricsMembers);
        Assert.AreEqual(2, report.Methods.Single().Complexity);
        // The raw display stays what Roslyn wrote, for unmatched listings.
        Assert.AreEqual(display, metrics.Single().DisplayName);
    }
}
