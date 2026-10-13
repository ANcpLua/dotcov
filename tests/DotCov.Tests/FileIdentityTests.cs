using System.Text;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

/// <summary>
/// The three file-identity modes of the library. Lexical, the default, never reads the disk: a
/// relative filename under several roots keeps a deterministic key, is listed as ambiguous, and
/// turns every gate into NoData. Probing reproduces 2.0.0 (<see cref="ParserSourceTests"/>), and
/// an explicit root resolves the ambiguity on any machine.
/// </summary>
public sealed class FileIdentityTests : IDisposable
{
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-identity-");

    public void Dispose() => _ws.Dispose();

    /// <summary>A workspace directory as a declared root: forward slashes, like the parser's keys.</summary>
    private string Root(string relative) => _ws.CreateDirectory(relative).Replace('\\', '/');

    private static Cobertura TwoRoots(string first, string second, params string[] files)
    {
        var doc = Cobertura.NewDoc().WithSource(first).WithSource(second);
        foreach (var file in files)
            doc.AddClass(file, "C", c => c.Line(1, hits: 1).Line(2, hits: 0));
        return doc;
    }

    [Test]
    public async Task Lexical_KeysUnderTheFirstRoot_ListsTheFile_AndNeverReadsTheDisk()
    {
        // The file exists only under the SECOND root. Probing would find it; lexical resolution
        // must not, or the same XML would key differently on another machine.
        var src = Root("src");
        var tests = Root("tests");
        _ws.Write("tests/test_app.py", "");

        var report = TwoRoots(src, tests, "test_app.py").Parse();

        await Assert.That(report.Files.Single().Path).IsEqualTo($"{src}/test_app.py");
        var ambiguous = await Assert.That(report.AmbiguousFiles).HasSingleItem();
        await Assert.That(ambiguous.Path).IsEqualTo($"{src}/test_app.py");
        await Assert.That(ambiguous.CandidateRoots).IsEquivalentTo([src, tests], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        var warning = await Assert.That(report.Warnings).HasSingleItem();
        await Assert.That(warning.Kind).IsEqualTo(CoverageWarningKind.FileIdentityAmbiguous);
        await Assert.That(warning.File).IsEqualTo($"{src}/test_app.py");
        await Assert.That(warning.Detail).IsEqualTo(
            $"relative filename 'test_app.py' could belong to any of 2 <source> roots ('{src}', '{tests}') - keyed under the first, identity unresolved");

        var probed = CoberturaParser.Parse(TwoRoots(src, tests, "test_app.py").ToStream(), identity: FileIdentity.ProbeSources);
        await Assert.That(probed.Files.Single().Path).IsEqualTo($"{tests}/test_app.py");
        await Assert.That(probed.AmbiguousFiles).IsEmpty();
    }

    [Test]
    public async Task Lexical_GateIsNoData_NamingFilesAndRoots_AndHowToResolveThem()
    {
        var one = TwoRoots("/a", "/b", "x.cs").Parse();
        var two = TwoRoots("/a", "/b", "x.cs", "y.cs").Parse();

        var gate = one.Evaluate(10);
        await Assert.That(gate.Outcome).IsEqualTo(GateOutcome.NoData);
        await Assert.That(gate.LineRate).IsEqualTo(0.5);   // measured, but not attributable
        await Assert.That(gate.Reason).IsEqualTo(
            "1 file has ambiguous identity across 2 source roots; re-run with --probe-sources on the machine that holds the sources or supply --source-root");
        await Assert.That(two.Evaluate(10).Reason).StartsWith("2 files have ambiguous identity across 2 source roots;");
        // A disabled gate stays disabled: nothing is asked of the numbers.
        await Assert.That(one.Evaluate(0).Outcome).IsEqualTo(GateOutcome.Disabled);
    }

    [Test]
    public async Task Lexical_OneFileInSeveralClassBlocks_IsListedAndWarnedOnce()
    {
        var report = Cobertura.NewDoc().WithSource("/a").WithSource("/b")
            .AddClass("x.cs", "C", c => c.Line(1, 1))
            .AddClass("x.cs", "C/<M>d__0", c => c.Line(2, 0))
            .Parse();

        await Assert.That(report.AmbiguousFiles.Select(static a => a.Path)).IsEquivalentTo(["/a/x.cs"]);
        await Assert.That(report.Warnings.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Lexical_SingleRootAndAbsoluteNames_AreNeverAmbiguous()
    {
        var single = Cobertura.NewDoc().WithSource("/a").AddClass("x.cs", c => c.Line(1, 1)).Parse();
        var absolute = TwoRoots("/a", "/b", "/elsewhere/x.cs").Parse();

        await Assert.That(single.AmbiguousFiles).IsEmpty();
        await Assert.That(absolute.AmbiguousFiles).IsEmpty();
        await Assert.That(absolute.Warnings).IsEmpty();
        await Assert.That(absolute.Evaluate(10).Outcome).IsEqualTo(GateOutcome.Pass);
    }

    [Test]
    public async Task SourceRoot_ResolvesAgainstTheChosenRoot_WithoutWarnings()
    {
        // Compared after normalization: a Windows spelling of a declared root matches it.
        var report = CoberturaParser.Parse(TwoRoots("C:/repo/src", "C:/repo/tests", "x.cs").ToStream(),
            identity: FileIdentity.FromSourceRoot(@"c:\repo\tests\"));

        await Assert.That(report.Files.Single().Path).IsEqualTo("C:/repo/tests/x.cs");
        await Assert.That(report.Warnings).IsEmpty();
        await Assert.That(report.AmbiguousFiles).IsEmpty();
        await Assert.That(report.Evaluate(10).Outcome).IsEqualTo(GateOutcome.Pass);
    }

    [Test]
    public async Task SourceRoot_TheNoOpRoot_LeavesRelativeNamesUnprefixed()
    {
        var report = CoberturaParser.Parse(TwoRoots(".", "/real", "app/main.py").ToStream(),
            identity: FileIdentity.FromSourceRoot("."));

        await Assert.That(report.Files.Single().Path).IsEqualTo("app/main.py");
        await Assert.That(report.AmbiguousFiles).IsEmpty();
    }

    [Test]
    public async Task SourceRoot_UndeclaredByAMultiRootReport_Throws_NamingTheReportAndItsRoots()
    {
        var input = ReportInput.FromBytes("job-a.xml", TwoRoots("/a", "/b", "x.cs").ToBytes());

        var fromInput = Assert.ThrowsExactly<UndeclaredSourceRootException>(
            () => CoberturaParser.Parse(input, identity: FileIdentity.FromSourceRoot("/c")));
        var fromStream = Assert.ThrowsExactly<UndeclaredSourceRootException>(
            () => CoberturaParser.Parse(TwoRoots(".", "/b", "x.cs").ToStream(), identity: FileIdentity.FromSourceRoot("/c")));

        await Assert.That(fromInput.Message).IsEqualTo("source root '/c' is not declared by job-a.xml (declared: '/a', '/b').");
        await Assert.That(fromInput.SourceRoot).IsEqualTo("/c");
        await Assert.That(fromInput.DeclaredRoots).IsEquivalentTo(["/a", "/b"]);
        await Assert.That(fromInput.SourceName).IsEqualTo("job-a.xml");
        await Assert.That(fromStream.Message).IsEqualTo("source root '/c' is not declared by the report (declared: '.', '/b').");
    }

    [Test]
    public async Task SourceRoot_DeclaredByNoReport_Throws_EvenWhereNothingIsAmbiguous()
    {
        // A flag nobody's report can use is a mistake, not a no-op.
        var a = ReportInput.FromBytes("a.xml", Cobertura.NewDoc().WithSource("/a").AddClass("x.cs", c => c.Line(1, 1)).ToBytes());
        var plain = ReportInput.FromBytes("plain.xml", Cobertura.NewDoc().AddClass("/abs/y.cs", c => c.Line(1, 1)).ToBytes());

        var ex = Assert.ThrowsExactly<UndeclaredSourceRootException>(
            () => CoberturaParser.Parse([a, plain], identity: FileIdentity.FromSourceRoot("/b")));
        var none = Assert.ThrowsExactly<UndeclaredSourceRootException>(
            () => CoberturaParser.Parse(plain, identity: FileIdentity.FromSourceRoot("/b")));

        await Assert.That(ex.Message).IsEqualTo("source root '/b' is not declared by any report (declared: '/a').");
        await Assert.That(ex.SourceName).IsNull();
        await Assert.That(none.Message).IsEqualTo("source root '/b' is not declared by any report (declared: none).");
    }

    [Test]
    public async Task SourceRoot_DeclaredByOneOfSeveralReports_ResolvesThatOne_AndLeavesTheOthers()
    {
        var single = ReportInput.FromBytes("single.xml",
            Cobertura.NewDoc().WithSource("/svc-a").AddClass("x.cs", c => c.Line(1, 1)).ToBytes());
        var multi = ReportInput.FromBytes("multi.xml", TwoRoots("/svc-b/src", "/svc-b/tests", "y.cs").ToBytes());

        var report = CoberturaParser.Parse([single, multi], identity: FileIdentity.FromSourceRoot("/svc-b/tests"));

        await Assert.That(report.Files.Select(static f => f.Path)).IsEquivalentTo(["/svc-a/x.cs", "/svc-b/tests/y.cs"]);
        await Assert.That(report.AmbiguousFiles).IsEmpty();
    }

    [Test]
    public async Task SourceRoot_WithoutAnyReport_IsNotAnError_TheGateSaysNoData()
    {
        var report = CoberturaParser.Parse([], identity: FileIdentity.FromSourceRoot("/b"));

        await Assert.That(report.Evaluate(10).Outcome).IsEqualTo(GateOutcome.NoData);
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    public async Task FromSourceRoot_Blank_Throws(string root)
    {
        var ex = Assert.Throws<ArgumentException>(() => FileIdentity.FromSourceRoot(root));

        await Assert.That(ex.ParamName).IsEqualTo("root");
    }

    [Test]
    public async Task Modes_AreValues_NotProcessState()
    {
        await Assert.That(FileIdentity.Lexical.Mode).IsEqualTo(FileIdentityMode.Lexical);
        await Assert.That(FileIdentity.Lexical.SourceRoot).IsNull();
        await Assert.That(FileIdentity.ProbeSources.Mode).IsEqualTo(FileIdentityMode.ProbeSources);
        await Assert.That(FileIdentity.FromSourceRoot("/x/").SourceRoot).IsEqualTo("/x");

        // Two parses with different modes, interleaved, each get their own resolution.
        var xml = TwoRoots("/a", "/b", "x.cs");
        var explicitRoot = CoberturaParser.Parse(xml.ToStream(), identity: FileIdentity.FromSourceRoot("/b"));
        var lexical = CoberturaParser.Parse(xml.ToStream());
        await Assert.That(explicitRoot.Files.Single().Path).IsEqualTo("/b/x.cs");
        await Assert.That(lexical.Files.Single().Path).IsEqualTo("/a/x.cs");
    }

    [Test]
    public async Task ParseAsync_FollowsTheSameModes()
    {
        var xml = TwoRoots("/a", "/b", "x.cs");

        var lexical = await CoberturaParser.ParseAsync(xml.ToStream());
        var chosen = await CoberturaParser.ParseAsync(xml.ToStream(), identity: FileIdentity.FromSourceRoot("/b"));

        await Assert.That(lexical.AmbiguousFiles.Single().Path).IsEqualTo("/a/x.cs");
        await Assert.That(chosen.Files.Single().Path).IsEqualTo("/b/x.cs");
        await Assert.That(async () => await CoberturaParser.ParseAsync(xml.ToStream(), identity: FileIdentity.FromSourceRoot("/c")))
            .ThrowsExactly<UndeclaredSourceRootException>();
        await Assert.That(async () => await CoberturaParser.ParseAsync(
                Cobertura.NewDoc().WithSource("/a").AddClass("x.cs", c => c.Line(1, 1)).ToStream(),
                identity: FileIdentity.FromSourceRoot("/c")))
            .ThrowsExactly<UndeclaredSourceRootException>();
    }

    [Test]
    public async Task Merge_UnionsAmbiguousFiles_AndExcludeDropsTheExcludedOnes()
    {
        var first = TwoRoots("/a", "/b", "x.cs", "obj/G.g.cs").Parse();
        var second = TwoRoots("/a", "/c", "x.cs").Parse();

        var merged = CoverageReport.Merge(first, second);
        var excluded = merged.Exclude(ExclusionRules.WellKnown);

        await Assert.That(merged.AmbiguousFiles.Select(static a => a.Path)).IsEquivalentTo(["/a/x.cs", "/a/obj/G.g.cs"]);
        await Assert.That(merged.AmbiguousFiles.Single(static a => a.Path == "/a/x.cs").CandidateRoots)
            .IsEquivalentTo(["/a", "/b", "/c"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(excluded.AmbiguousFiles.Select(static a => a.Path)).IsEquivalentTo(["/a/x.cs"]);
        await Assert.That(excluded.Evaluate(10).Reason).StartsWith("1 file has ambiguous identity across 3 source roots;");
    }

    // ── The method-level path and the CRAP gate ──

    private static Cobertura TwoRootsWithMethods(string file) => Cobertura.NewDoc().WithSource("/a").WithSource("/b")
        .AddClass(file, "Ns.C", c => c.Method("M", "()", "1", m => m.Line(1, 1)));

    [Test]
    public async Task ParseMethods_Lexical_ListsTheFile_AndTheCrapGateIsNoData()
    {
        var methods = CoberturaParser.ParseMethods(TwoRootsWithMethods("x.cs").ToStream());

        await Assert.That(methods.Methods.Single().File).IsEqualTo("/a/x.cs");
        await Assert.That(methods.AmbiguousFiles.Single().Path).IsEqualTo("/a/x.cs");
        var gate = CrapAnalysis.Analyze(methods).Evaluate(30);
        await Assert.That(gate.Outcome).IsEqualTo(GateOutcome.NoData);
        await Assert.That(gate.Reason).StartsWith("1 file has ambiguous identity across 2 source roots;");
        // An infinite threshold disables the gate before the identity is asked about.
        await Assert.That(CrapAnalysis.Analyze(methods).Evaluate(double.PositiveInfinity).Outcome).IsEqualTo(GateOutcome.Disabled);
    }

    [Test]
    public async Task ParseMethods_ExplicitRoot_IsAMeasuredCrapPass()
    {
        var input = ReportInput.FromBytes("methods.xml", TwoRootsWithMethods("x.cs").ToBytes());

        var methods = CoberturaParser.ParseMethods(input, identity: FileIdentity.FromSourceRoot("/b"));

        await Assert.That(methods.Methods.Single().File).IsEqualTo("/b/x.cs");
        await Assert.That(CrapAnalysis.Analyze(methods).Evaluate(30).Outcome).IsEqualTo(GateOutcome.Pass);
        await Assert.That(() => CoberturaParser.ParseMethods(TwoRootsWithMethods("x.cs").ToStream(),
                identity: FileIdentity.FromSourceRoot("/z")))
            .ThrowsExactly<UndeclaredSourceRootException>();
        await Assert.That(() => CoberturaParser.ParseMethods(
                Cobertura.NewDoc().WithSource("/a").AddClass("x.cs", "C", c => c.Method("M", "()", "1", m => m.Line(1, 1))).ToStream(),
                identity: FileIdentity.FromSourceRoot("/z")))
            .ThrowsExactly<UndeclaredSourceRootException>();
    }

    [Test]
    public async Task ParseMethods_AmbiguousAcrossInputs_AreUnioned_AndExcludeDropsThem()
    {
        var first = ReportInput.FromBytes("1.xml", TwoRootsWithMethods("x.cs").ToBytes());
        var second = ReportInput.FromBytes("2.xml", Cobertura.NewDoc().WithSource("/a").WithSource("/c")
            .AddClass("x.cs", "Ns.C", c => c.Method("N", "()", "1", m => m.Line(2, 1))).ToBytes());

        var methods = CoberturaParser.ParseMethods([first, second]);
        var excluded = methods.Exclude(["x.cs"], []);

        var ambiguous = await Assert.That(methods.AmbiguousFiles).HasSingleItem();
        await Assert.That(ambiguous.CandidateRoots).IsEquivalentTo(["/a", "/b", "/c"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(excluded.AmbiguousFiles).IsEmpty();
        await Assert.That(excluded.Methods).IsEmpty();
        await Assert.That(excluded.Exclusion.ExcludedFiles).IsEquivalentTo(["/a/x.cs"]);
    }

    [Test]
    public async Task Probe_StaysTheOptIn_ItsResultDependsOnTheReadingMachine()
    {
        // Same XML, two machines: one holds the file under the second root, the other holds
        // nothing. That difference is exactly why probing is opt-in.
        var holder = Root("holder/b");
        _ws.Write("holder/b/x.cs", "");
        var holderA = holder[..^"/b".Length] + "/a";
        var xml = Encoding.UTF8.GetBytes($"""
            <coverage><sources><source>{holderA}</source><source>{holder}</source></sources>
            <packages><package><classes><class name="C" filename="x.cs"><lines><line number="1" hits="1"/></lines></class>
            </classes></package></packages></coverage>
            """);

        var probed = CoberturaParser.Parse(new MemoryStream(xml), identity: FileIdentity.ProbeSources);
        Directory.Delete(holder, recursive: true);
        var probedElsewhere = CoberturaParser.Parse(new MemoryStream(xml), identity: FileIdentity.ProbeSources);

        await Assert.That(probed.Files.Single().Path).IsEqualTo($"{holder}/x.cs");
        await Assert.That(probedElsewhere.Files.Single().Path).IsEqualTo($"{holderA}/x.cs");
    }
}
