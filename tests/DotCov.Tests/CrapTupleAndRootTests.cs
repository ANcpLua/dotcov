using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

public sealed class CrapTupleAndRootTests
{
    [Test]
    [Arguments("(int x, string y)", "System.ValueTuple`2<System.Int32,System.String>")]
    [Arguments("( int , string )", "System.ValueTuple`2<System.Int32,System.String>")]
    [Arguments("(int @number, string snake_case)", "System.ValueTuple`2<System.Int32,System.String>")]
    [Arguments("(App.Widget, string)", "System.ValueTuple`2<App.Widget,System.String>")]
    [Arguments("((int a,string b) inner,bool ok)", "System.ValueTuple`2<System.ValueTuple`2<System.Int32,System.String>,System.Boolean>")]
    [Arguments("(int[,] grid,List<string> names)[]", "System.ValueTuple`2<System.Int32[,],System.Collections.Generic.List`1<System.String>>[]")]
    [Arguments("(int x,string y)?", "System.Nullable`1<System.ValueTuple`2<System.Int32,System.String>>")]
    [Arguments("Dictionary<string,(int,string)>", "System.Collections.Generic.Dictionary`2<System.String,System.ValueTuple`2<System.Int32,System.String>>")]
    [Arguments("(int,int,int,int,int,int,int,int)", "System.ValueTuple`8<System.Int32,System.Int32,System.Int32,System.Int32,System.Int32,System.Int32,System.Int32,System.ValueTuple`1<System.Int32>>")]
    public async Task TupleParameters_MatchRuntimeMetadata(string source, string runtime)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c
            .Method("M", $"({runtime})", null, m => m.Line(1, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method($"void A.M({source} value)", 2, 1));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        await Assert.That(report.UnmatchedMetricsMembers).IsEmpty();
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.Pass);
    }

    [Test]
    [Arguments("left")]
    [Arguments("right")]
    [Arguments("deep")]
    [Arguments("empty")]
    public async Task InvalidTupleStructure_IsRejected(string shape)
    {
        var type = shape switch
        {
            "left" => "(int",
            "right" => "int)",
            "empty" => "( , int)",
            _ => new string('(', 65) + "int" + string.Concat(Enumerable.Repeat(",int)", 65))
        };
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c
            .Method("M", $"({type})", "1", m => m.Line(1, 1))).ToBytes());
        var parsed = CoberturaParser.ParseMethods(input);
        var error = Assert.Throws<InvalidDataException>(() => CrapAnalysis.Analyze(parsed));
        await Assert.That(error.Message).IsNotEmpty();
    }

    [Test]
    public async Task TupleDepthBoundary_RemainsMeasurable()
    {
        var source = "int";
        var runtime = "System.Int32";
        for (var i = 0; i < 64; i++)
        {
            source = $"({source},int)";
            runtime = $"System.ValueTuple`2<{runtime},System.Int32>";
        }
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c
            .Method("M", $"({runtime})", null, m => m.Line(1, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method($"void A.M({source} value)", 2, 1));
        await Assert.That(CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics).Evaluate(30).Outcome)
            .IsEqualTo(GateOutcome.Pass);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RootedExclusion_AppliesToRelativeMetricsOnlyFiles(bool keep)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().WithSource("/repo")
            .AddClass("src/Kept.cs", "App.Kept", c => c.Method("M", "()", "1", m => m.Line(1, 1))).ToBytes());
        var report = CoberturaParser.ParseMethods(input).Exclude(["/repo/src/Generated.g.cs"],
            keep ? ["/repo/src/Generated.g.cs"] : []);
        var metrics = CodeMetricsReportBuilder.ParseType("App", "Generated", m => m.Method("void Generated.M()", 2, 1))
            .Select(m => m with { File = "src/Generated.g.cs" }).ToArray();
        var analysis = CrapAnalysis.Analyze(report, metrics);
        await Assert.That(analysis.UnmatchedMetricsMembers.Count).IsEqualTo(keep ? 1 : 0);
        await Assert.That(analysis.Evaluate(30).Outcome).IsEqualTo(keep ? GateOutcome.NoData : GateOutcome.Pass);
    }

    [Test]
    public async Task AmbiguousCustomTypeNames_StayUnresolved()
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c
            .Method("M", "(One.Widget)", null, m => m.Line(1, 1))
            .Method("M", "(Two.Widget)", null, m => m.Line(2, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m
            .Method("void A.M(Widget value)", 2, 1).Method("void A.M(Two.Widget value)", 3, 1));
        await Assert.That(CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics).Evaluate(30).Outcome)
            .IsEqualTo(GateOutcome.NoData);
    }
}
