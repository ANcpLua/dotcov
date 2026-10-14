using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

/// <summary>
/// Roslyn writes Visual Basic members as <c>Sub A.M(value As Integer)</c> and
/// <c>Function A.M(value As Integer) As Long</c>; they match Coverlet's IL signatures like C# displays.
/// </summary>
public sealed class CrapVisualBasicMetricsTests
{
    [Test]
    [Arguments("Sub A.M(value As Integer)", "System.Int32")]
    [Arguments("Sub A.M(ByRef value As Integer)", "System.Int32&")]
    [Arguments("Sub A.M(Optional value As Integer = 5)", "System.Int32")]
    [Arguments("Sub A.M([value As Integer = 5])", "System.Int32")]
    [Arguments("Sub A.M(when As Date)", "System.DateTime")]
    [Arguments("Sub A.M(values As Integer())", "System.Int32[]")]
    [Arguments("Sub A.M(grid As Integer(,))", "System.Int32[,]")]
    [Arguments("Sub A.M(ParamArray values As String())", "System.String[]")]
    [Arguments("Sub A.M(values As List(Of Integer))", "System.Collections.Generic.List`1<System.Int32>")]
    [Arguments("Sub A.M(map As Dictionary(Of String, List(Of Long)))",
        "System.Collections.Generic.Dictionary`2<System.String,System.Collections.Generic.List`1<System.Int64>>")]
    [Arguments("Function A.M(value As Integer) As List(Of Long)", "System.Int32")]
    public async Task VisualBasicOverloads_MatchTheirOwnRows(string display, string ilType)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.vb", "App.A", c => c
            .Method("M", $"({ilType})", null, m => m.Line(1, 1))
            .Method("M", "(System.Boolean)", null, m => m.Line(2, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m
            .Method(display, 2, 1).Method("Sub A.M(flag As Boolean)", 7, 1));

        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);

        await Assert.That(report.UnmatchedMetricsMembers).IsEmpty();
        await Assert.That(report.Methods.Select(m => m.Complexity)).IsEquivalentTo([2, 7]);
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.Pass);
    }

    // A tuple's parenthesis opens the type or follows a space, and a function display may lack its
    // return clause: the signature stays unknown and the sole method matches by name.
    [Test]
    [Arguments("Sub A.M(pair As (Integer, String))")]
    [Arguments("Sub A.M(items As List(Of (Integer, String)))")]
    [Arguments("Function A.M")]
    public async Task UnknownVisualBasicShapes_MatchASoleMethod(string display)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.vb", "App.A", c => c
            .Method("M", "(System.ValueTuple`2<System.Int32,System.String>)", null, m => m.Line(1, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method(display, 2, 1));

        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);

        await Assert.That(report.UnmatchedMetricsMembers).IsEmpty();
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.Pass);
    }

    [Test]
    public async Task UnbalancedVisualBasicType_IsAnInputErrorNamingTheMember()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("Sub A.M(x As <Integer))", 2, 1)));

        await Assert.That(error.Message).StartsWith("Metrics member display 'Sub A.M(x As <Integer))': ");
    }

    [Test]
    [Arguments("Sub A.M(Of T)(value As T)", "M", "(T)")]
    [Arguments("Function A.M(Of T)(value As T) As T", "M", "(T)")]
    [Arguments("Sub A.New(value As Integer)", ".ctor", "(System.Int32)")]
    public async Task VisualBasicGenericMethodsAndConstructors_MatchTheirMethod(string display, string method, string signature)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.vb", "App.A", c => c
            .Method(method, signature, null, m => m.Line(1, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method(display, 2, 1));

        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);

        await Assert.That(report.UnmatchedMetricsMembers).IsEmpty();
        await Assert.That(report.Methods.Single().Complexity).IsEqualTo(2);
        // The raw display stays what Roslyn wrote, for unmatched listings.
        await Assert.That(metrics.Single().DisplayName).IsEqualTo(display);
    }
}
