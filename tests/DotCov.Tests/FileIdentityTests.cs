using System.Text;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;
/// <summary>
/// The three file-identity modes of the library. Lexical, the default, never reads the disk: a
/// relative filename under several roots keeps a deterministic key, is listed as ambiguous, and
/// turns every gate into NoData. Probing reproduces 2.0.0 (<see cref = "ParserSourceTests"/>), and
/// an explicit root resolves the ambiguity on any machine.
/// </summary>
[TestClass]
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

    [TestMethod]
    public void Lexical_KeysUnderTheFirstRoot_ListsTheFile_AndNeverReadsTheDisk()
    {
        // The file exists only under the SECOND root. Probing would find it; lexical resolution
        // must not, or the same XML would key differently on another machine.
        var src = Root("src");
        var tests = Root("tests");
        _ws.Write("tests/test_app.py", "");
        var report = TwoRoots(src, tests, "test_app.py").Parse();
        Assert.AreEqual($"{src}/test_app.py", report.Files.Single().Path);
        var ambiguous = Assert.ContainsSingle(report.AmbiguousFiles);
        Assert.AreEqual($"{src}/test_app.py", ambiguous.Path);
        Assert.AreSequenceEqual([src, tests], ambiguous.CandidateRoots, SequenceOrder.InOrder);
        var warning = Assert.ContainsSingle(report.Warnings);
        Assert.AreEqual(CoverageWarningKind.FileIdentityAmbiguous, warning.Kind);
        Assert.AreEqual($"{src}/test_app.py", warning.File);
        Assert.AreEqual($"relative filename 'test_app.py' could belong to any of 2 <source> roots ('{src}', '{tests}') - keyed under the first, identity unresolved", warning.Detail);
        // Exercise the synchronous parser contract alongside awaited assertions.
        // ReSharper disable once MethodHasAsyncOverload
        var probed = CoberturaParser.Parse(TwoRoots(src, tests, "test_app.py").ToStream(), identity: FileIdentity.ProbeSources);
        Assert.AreEqual($"{tests}/test_app.py", probed.Files.Single().Path);
        Assert.IsEmpty(probed.AmbiguousFiles);
    }

    [TestMethod]
    public void Lexical_GateIsNoData_NamingFilesAndRoots_AndHowToResolveThem()
    {
        var one = TwoRoots("/a", "/b", "x.cs").Parse();
        var two = TwoRoots("/a", "/b", "x.cs", "y.cs").Parse();
        var gate = one.Evaluate(10);
        Assert.AreEqual(GateOutcome.NoData, gate.Outcome);
        Assert.AreEqual(0.5, gate.LineRate); // measured, but not attributable
        Assert.AreEqual("1 file has ambiguous identity across 2 source roots; re-run with --probe-sources on the machine that holds the sources or supply --source-root", gate.Reason);
        Assert.StartsWith("2 files have ambiguous identity across 2 source roots;", two.Evaluate(10).Reason);
        // A disabled gate stays disabled: nothing is asked of the numbers.
        Assert.AreEqual(GateOutcome.Disabled, one.Evaluate(0).Outcome);
    }

    [TestMethod]
    public void Lexical_OneFileInSeveralClassBlocks_IsListedAndWarnedOnce()
    {
        var report = Cobertura.NewDoc().WithSource("/a").WithSource("/b").AddClass("x.cs", "C", c => c.Line(1, 1)).AddClass("x.cs", "C/<M>d__0", c => c.Line(2, 0)).Parse();
        Assert.AreSequenceEqual(["/a/x.cs"], report.AmbiguousFiles.Select(static a => a.Path), SequenceOrder.InAnyOrder);
        Assert.AreEqual(1, report.Warnings.Count);
    }

    [TestMethod]
    public void Lexical_SingleRootAndAbsoluteNames_AreNeverAmbiguous()
    {
        var single = Cobertura.NewDoc().WithSource("/a").AddClass("x.cs", c => c.Line(1, 1)).Parse();
        var absolute = TwoRoots("/a", "/b", "/elsewhere/x.cs").Parse();
        Assert.IsEmpty(single.AmbiguousFiles);
        Assert.IsEmpty(absolute.AmbiguousFiles);
        Assert.IsEmpty(absolute.Warnings);
        Assert.AreEqual(GateOutcome.Pass, absolute.Evaluate(10).Outcome);
    }

    [TestMethod]
    public void SourceRoot_ResolvesAgainstTheChosenRoot_WithoutWarnings()
    {
        // Compared after normalization: a Windows spelling of a declared root matches it.
        // Exercise the synchronous parser contract alongside awaited assertions.
        // ReSharper disable once MethodHasAsyncOverload
        var report = CoberturaParser.Parse(TwoRoots("C:/repo/src", "C:/repo/tests", "x.cs").ToStream(), identity: FileIdentity.FromSourceRoot(@"c:\repo\tests\"));
        Assert.AreEqual("C:/repo/tests/x.cs", report.Files.Single().Path);
        Assert.IsEmpty(report.Warnings);
        Assert.IsEmpty(report.AmbiguousFiles);
        Assert.AreEqual(GateOutcome.Pass, report.Evaluate(10).Outcome);
    }

    [TestMethod]
    public void SourceRoot_TheNoOpRoot_LeavesRelativeNamesUnprefixed()
    {
        // Exercise the synchronous parser contract alongside awaited assertions.
        // ReSharper disable once MethodHasAsyncOverload
        var report = CoberturaParser.Parse(TwoRoots(".", "/real", "app/main.py").ToStream(), identity: FileIdentity.FromSourceRoot("."));
        Assert.AreEqual("app/main.py", report.Files.Single().Path);
        Assert.IsEmpty(report.AmbiguousFiles);
    }

    [TestMethod]
    public void SourceRoot_UndeclaredByAMultiRootReport_Throws_NamingTheReportAndItsRoots()
    {
        var input = ReportInput.FromBytes("job-a.xml", TwoRoots("/a", "/b", "x.cs").ToBytes());
        var fromInput = Assert.ThrowsExactly<UndeclaredSourceRootException>(() => CoberturaParser.Parse(input, identity: FileIdentity.FromSourceRoot("/c")));
        var fromStream = Assert.ThrowsExactly<UndeclaredSourceRootException>(() => CoberturaParser.Parse(TwoRoots(".", "/b", "x.cs").ToStream(), identity: FileIdentity.FromSourceRoot("/c")));
        Assert.AreEqual("source root '/c' is not declared by job-a.xml (declared: '/a', '/b').", fromInput.Message);
        Assert.AreEqual("/c", fromInput.SourceRoot);
        Assert.AreSequenceEqual(["/a", "/b"], fromInput.DeclaredRoots, SequenceOrder.InAnyOrder);
        Assert.AreEqual("job-a.xml", fromInput.SourceName);
        Assert.AreEqual("source root '/c' is not declared by the report (declared: '.', '/b').", fromStream.Message);
    }

    [TestMethod]
    public void SourceRoot_DeclaredByNoReport_Throws_EvenWhereNothingIsAmbiguous()
    {
        // A flag nobody's report can use is a mistake, not a no-op.
        var a = ReportInput.FromBytes("a.xml", Cobertura.NewDoc().WithSource("/a").AddClass("x.cs", c => c.Line(1, 1)).ToBytes());
        var plain = ReportInput.FromBytes("plain.xml", Cobertura.NewDoc().AddClass("/abs/y.cs", c => c.Line(1, 1)).ToBytes());
        var ex = Assert.ThrowsExactly<UndeclaredSourceRootException>(() => CoberturaParser.Parse([a, plain], identity: FileIdentity.FromSourceRoot("/b")));
        var none = Assert.ThrowsExactly<UndeclaredSourceRootException>(() => CoberturaParser.Parse(plain, identity: FileIdentity.FromSourceRoot("/b")));
        Assert.AreEqual("source root '/b' is not declared by any report (declared: '/a').", ex.Message);
        Assert.IsNull(ex.SourceName);
        Assert.AreEqual("source root '/b' is not declared by any report (declared: none).", none.Message);
    }

    [TestMethod]
    public void SourceRoot_DeclaredByOneOfSeveralReports_ResolvesThatOne_AndLeavesTheOthers()
    {
        var single = ReportInput.FromBytes("single.xml", Cobertura.NewDoc().WithSource("/svc-a").AddClass("x.cs", c => c.Line(1, 1)).ToBytes());
        var multi = ReportInput.FromBytes("multi.xml", TwoRoots("/svc-b/src", "/svc-b/tests", "y.cs").ToBytes());
        var report = CoberturaParser.Parse([single, multi], identity: FileIdentity.FromSourceRoot("/svc-b/tests"));
        Assert.AreSequenceEqual(["/svc-a/x.cs", "/svc-b/tests/y.cs"], report.Files.Select(static f => f.Path), SequenceOrder.InAnyOrder);
        Assert.IsEmpty(report.AmbiguousFiles);
    }

    [TestMethod]
    public void SourceRoot_WithoutAnyReport_IsNotAnError_TheGateSaysNoData()
    {
        var report = CoberturaParser.Parse([], identity: FileIdentity.FromSourceRoot("/b"));
        Assert.AreEqual(GateOutcome.NoData, report.Evaluate(10).Outcome);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public void FromSourceRoot_Blank_Throws(string root)
    {
        var ex = Assert.Throws<ArgumentException>(() => FileIdentity.FromSourceRoot(root));
        Assert.AreEqual("root", ex.ParamName);
    }

    [TestMethod]
    public void Modes_AreValues_NotProcessState()
    {
        Assert.AreEqual(FileIdentityMode.Lexical, FileIdentity.Lexical.Mode);
        Assert.IsNull(FileIdentity.Lexical.SourceRoot);
        Assert.AreEqual(FileIdentityMode.ProbeSources, FileIdentity.ProbeSources.Mode);
        Assert.AreEqual("/x", FileIdentity.FromSourceRoot("/x/").SourceRoot);
        // Two parses with different modes, interleaved, each get their own resolution.
        var xml = TwoRoots("/a", "/b", "x.cs");
        // Exercise the synchronous parser contract alongside awaited assertions.
        // ReSharper disable once MethodHasAsyncOverload
        var explicitRoot = CoberturaParser.Parse(xml.ToStream(), identity: FileIdentity.FromSourceRoot("/b"));
        // Exercise the synchronous parser contract alongside awaited assertions.
        // ReSharper disable once MethodHasAsyncOverload
        var lexical = CoberturaParser.Parse(xml.ToStream());
        Assert.AreEqual("/b/x.cs", explicitRoot.Files.Single().Path);
        Assert.AreEqual("/a/x.cs", lexical.Files.Single().Path);
    }

    [TestMethod]
    public async Task ParseAsync_FollowsTheSameModes()
    {
        var xml = TwoRoots("/a", "/b", "x.cs");
        var lexical = await CoberturaParser.ParseAsync(xml.ToStream());
        var chosen = await CoberturaParser.ParseAsync(xml.ToStream(), identity: FileIdentity.FromSourceRoot("/b"));
        Assert.AreEqual("/a/x.cs", lexical.AmbiguousFiles.Single().Path);
        Assert.AreEqual("/b/x.cs", chosen.Files.Single().Path);
        await Assert.ThrowsExactlyAsync<UndeclaredSourceRootException>(async () => await CoberturaParser.ParseAsync(xml.ToStream(), identity: FileIdentity.FromSourceRoot("/c")));
        await Assert.ThrowsExactlyAsync<UndeclaredSourceRootException>(async () => await CoberturaParser.ParseAsync(Cobertura.NewDoc().WithSource("/a").AddClass("x.cs", c => c.Line(1, 1)).ToStream(), identity: FileIdentity.FromSourceRoot("/c")));
    }

    [TestMethod]
    public void Merge_UnionsAmbiguousFiles_AndExcludeDropsTheExcludedOnes()
    {
        var first = TwoRoots("/a", "/b", "x.cs", "obj/G.g.cs").Parse();
        var second = TwoRoots("/a", "/c", "x.cs").Parse();
        var merged = CoverageReport.Merge(first, second);
        var excluded = merged.Exclude(ExclusionRules.WellKnown);
        Assert.AreSequenceEqual(["/a/x.cs", "/a/obj/G.g.cs"], merged.AmbiguousFiles.Select(static a => a.Path), SequenceOrder.InAnyOrder);
        Assert.AreSequenceEqual(["/a", "/b", "/c"], merged.AmbiguousFiles.Single(static a => a.Path == "/a/x.cs").CandidateRoots, SequenceOrder.InOrder);
        Assert.AreSequenceEqual(["/a/x.cs"], excluded.AmbiguousFiles.Select(static a => a.Path), SequenceOrder.InAnyOrder);
        Assert.StartsWith("1 file has ambiguous identity across 3 source roots;", excluded.Evaluate(10).Reason);
    }

    // ── The method-level path and the CRAP gate ──
    private static Cobertura TwoRootsWithMethods(string file) => Cobertura.NewDoc().WithSource("/a").WithSource("/b").AddClass(file, "Ns.C", c => c.Method("M", "()", "1", m => m.Line(1, 1)));
    [TestMethod]
    public void ParseMethods_Lexical_ListsTheFile_AndTheCrapGateIsNoData()
    {
        var methods = CoberturaParser.ParseMethods(TwoRootsWithMethods("x.cs").ToStream());
        Assert.AreEqual("/a/x.cs", methods.Methods.Single().File);
        Assert.AreEqual("/a/x.cs", methods.AmbiguousFiles.Single().Path);
        var gate = CrapAnalysis.Analyze(methods).Evaluate(30);
        Assert.AreEqual(GateOutcome.NoData, gate.Outcome);
        Assert.StartsWith("1 file has ambiguous identity across 2 source roots;", gate.Reason);
        // An infinite threshold disables the gate before the identity is asked about.
        Assert.AreEqual(GateOutcome.Disabled, CrapAnalysis.Analyze(methods).Evaluate(double.PositiveInfinity).Outcome);
    }

    [TestMethod]
    public void ParseMethods_ExplicitRoot_IsAMeasuredCrapPass()
    {
        var input = ReportInput.FromBytes("methods.xml", TwoRootsWithMethods("x.cs").ToBytes());
        var methods = CoberturaParser.ParseMethods(input, identity: FileIdentity.FromSourceRoot("/b"));
        Assert.AreEqual("/b/x.cs", methods.Methods.Single().File);
        Assert.AreEqual(GateOutcome.Pass, CrapAnalysis.Analyze(methods).Evaluate(30).Outcome);
        Assert.ThrowsExactly<UndeclaredSourceRootException>(() => CoberturaParser.ParseMethods(TwoRootsWithMethods("x.cs").ToStream(), identity: FileIdentity.FromSourceRoot("/z")));
        Assert.ThrowsExactly<UndeclaredSourceRootException>(() => CoberturaParser.ParseMethods(Cobertura.NewDoc().WithSource("/a").AddClass("x.cs", "C", c => c.Method("M", "()", "1", m => m.Line(1, 1))).ToStream(), identity: FileIdentity.FromSourceRoot("/z")));
    }

    [TestMethod]
    public void ParseMethods_AmbiguousAcrossInputs_AreUnioned_AndExcludeDropsThem()
    {
        var first = ReportInput.FromBytes("1.xml", TwoRootsWithMethods("x.cs").ToBytes());
        var second = ReportInput.FromBytes("2.xml", Cobertura.NewDoc().WithSource("/a").WithSource("/c").AddClass("x.cs", "Ns.C", c => c.Method("N", "()", "1", m => m.Line(2, 1))).ToBytes());
        var methods = CoberturaParser.ParseMethods([first, second]);
        var excluded = methods.Exclude(["x.cs"], []);
        var ambiguous = Assert.ContainsSingle(methods.AmbiguousFiles);
        Assert.AreSequenceEqual(["/a", "/b", "/c"], ambiguous.CandidateRoots, SequenceOrder.InOrder);
        Assert.IsEmpty(excluded.AmbiguousFiles);
        Assert.IsEmpty(excluded.Methods);
        Assert.AreSequenceEqual(["/a/x.cs"], excluded.Exclusion.ExcludedFiles, SequenceOrder.InAnyOrder);
    }

    [TestMethod]
    public void Probe_StaysTheOptIn_ItsResultDependsOnTheReadingMachine()
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
        // Exercise the synchronous parser contract alongside awaited assertions.
        // ReSharper disable once MethodHasAsyncOverload
        var probed = CoberturaParser.Parse(new MemoryStream(xml), identity: FileIdentity.ProbeSources);
        Directory.Delete(holder, recursive: true);
        // Exercise the synchronous parser contract alongside awaited assertions.
        // ReSharper disable once MethodHasAsyncOverload
        var probedElsewhere = CoberturaParser.Parse(new MemoryStream(xml), identity: FileIdentity.ProbeSources);
        Assert.AreEqual($"{holder}/x.cs", probed.Files.Single().Path);
        Assert.AreEqual($"{holderA}/x.cs", probedElsewhere.Files.Single().Path);
    }
}
