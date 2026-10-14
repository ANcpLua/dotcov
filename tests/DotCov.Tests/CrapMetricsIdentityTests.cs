using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

public sealed class CrapMetricsIdentityTests
{
    [Test]
    [Arguments("One.List`1<System.Int32>", "Two.List`1<System.Int32>")]
    [Arguments("List`1<System.Int32>", "Two.List`1<System.Int32>")]
    public async Task GenericQualificationCollisions_CannotProveCompleteCoverage(string first, string second)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c
            .Method("M", $"({first})", null, m => m.Line(1, 1))
            .Method("M", $"({second})", null, m => m.Line(2, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m
            .Method("void A.M(List<int> x)", 2, 1).Method("void A.M(Two.List<int> x)", 3, 1));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.NoData);
    }

    [Test]
    public async Task ConstructedGenericArguments_KeepNestedTypesArraysAndReferences()
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c
            .Method("M", "(System.Collections.Generic.Dictionary`2<System.String,System.Collections.Generic.List`1<System.Int32[]>>&)", null, m => m.Line(1, 1)))
            .ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m
            .Method("void A.M(ref Dictionary<string,List<int[]>> x)", 2, 1));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        await Assert.That(report.UnmatchedMetricsMembers).IsEmpty();
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.Pass);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConflictingSingleSignature_CannotBorrowAnotherMethodsMeasurement(bool includeMatch)
    {
        var doc = Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c =>
        {
            c.Method("M", "(System.String)", null, m => m.Line(1, 1));
            if (includeMatch) c.Method("M", "(System.Int32)", null, m => m.Line(2, 1));
        });
        using var input = new MemoryStream(doc.ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("void A.M(int x)", 2, 1));
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        await Assert.That(report.Unscored.Count).IsEqualTo(1);
        await Assert.That(report.UnmatchedMetricsMembers.Count).IsEqualTo(includeMatch ? 0 : 1);
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.NoData);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SourceIdentity_MatchesSeparateFilesWithoutReusingRows(bool includeSecondMetrics)
    {
        using var input = new MemoryStream(Cobertura.NewDoc()
            .AddClass("other/A.cs", "App.A", c => c.Method("M", "()", null, m => m.Line(1, 1)))
            .AddClass("src/A.cs", "App.A", c => c.Method("M", "()", null, m => m.Line(2, 1))).ToBytes());
        var first = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("void A.M()", 2, 1))
            .Select(m => m with { File = "src/A.cs" });
        var second = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("void A.M()", 7, 1))
            .Select(m => m with { File = "other/A.cs" });
        var metrics = (includeSecondMetrics ? first.Concat(second) : first).ToArray();
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        await Assert.That(report.Unscored.Count).IsEqualTo(includeSecondMetrics ? 0 : 1);
        await Assert.That(report.Methods.Select(m => m.Complexity)).IsEquivalentTo(includeSecondMetrics ? [7, 2] : new[] { 2 });
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(includeSecondMetrics ? GateOutcome.Pass : GateOutcome.NoData);
    }

    [Test]
    [Arguments(null, "src/./A.cs", "src/A.cs")]
    [Arguments(".", "src/A.cs", "src/A.cs")]
    [Arguments("/", "src/A.cs", "src/A.cs")]
    [Arguments("/repo", "src/A.cs", "src/A.cs")]
    [Arguments("/repo", "/repo/src/./A.cs", "src/A.cs")]
    [Arguments("c:/repo", "c:\\repo\\src\\A.cs", "src/A.cs")]
    [Arguments("c:/repo", "src/A.cs", "src/A.cs")]
    [Arguments("/repo", "A", "A")]
    public async Task EquivalentSourceConventions_Match(string? root, string metricsFile, string coverageFile)
    {
        var doc = Cobertura.NewDoc().AddClass(coverageFile, "App.A", c => c.Method("M", "()", null, m => m.Line(1, 1)));
        if (root is not null) doc.WithSource(root);
        using var input = new MemoryStream(doc.ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("void A.M()", 2, 1))
            .Select(m => m with { File = metricsFile }).ToArray();
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        await Assert.That(report.UnmatchedMetricsMembers).IsEmpty();
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.Pass);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SourceBearingPropertyFallback_StaysWithinItsFile(bool rightFile)
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass(rightFile ? "src/A.cs" : "other/A.cs", "App.A", c => c
            .Method("get_P", "()", null, m => m.Line(1, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Property("int A.P", 2))
            .Select(m => m with { File = "src/A.cs" }).ToArray();
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(rightFile ? GateOutcome.Pass : GateOutcome.NoData);
    }

    [Test]
    public async Task KnownSourceWithDifferentMember_CannotProvidePropertyFallback()
    {
        using var input = new MemoryStream(Cobertura.NewDoc().AddClass("src/A.cs", "App.A", c => c
            .Method("get_Missing", "()", null, m => m.Line(1, 1))).ToBytes());
        var metrics = CodeMetricsReportBuilder.ParseType("App", "A", m => m.Method("void A.M()", 2, 1))
            .Select(m => m with { File = "src/A.cs" }).ToArray();
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        await Assert.That(report.UnmatchedMetricsMembers.Count).IsEqualTo(1);
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(GateOutcome.NoData);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task LegacyGenericMetrics_OnlyRecoverUnambiguousTypeIdentity(bool ambiguous)
    {
        var doc = Cobertura.NewDoc().AddClass("src/A.cs", "App.Result`1", c => c.Method("M", "()", null, m => m.Line(1, 1)));
        if (ambiguous) doc.AddClass("src/B.cs", "App.Result", c => c.Method("M", "()", null, m => m.Line(2, 1)));
        using var input = new MemoryStream(doc.ToBytes());
        CodeMetricsMember[] metrics = [new("App.Result", "M", CodeMetricsMemberKind.Method, 0, 2, "void Result.M()")];
        var report = CrapAnalysis.Analyze(CoberturaParser.ParseMethods(input), metrics);
        await Assert.That(report.Unscored.Count).IsEqualTo(ambiguous ? 1 : 0);
        await Assert.That(report.Evaluate(30).Outcome).IsEqualTo(ambiguous ? GateOutcome.NoData : GateOutcome.Pass);
    }
}
