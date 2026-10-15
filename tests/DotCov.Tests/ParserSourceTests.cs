using System.Text;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;
[TestClass]
public sealed class ParserSourceTests
{
    private static CoverageReport ParseXml(string xml, FileIdentity? identity = null)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return CoberturaParser.Parse(stream, identity: identity);
    }

    /// <summary>One class per filename, with optional <c>&lt;source&gt;</c> roots (raw, unescaped).</summary>
    private static string Doc(string sources, params string[] filenames) => $"""
         <?xml version="1.0"?>
         <coverage>{sources}<packages><package><classes>
           {string.Concat(filenames.Select(f => $"""<class name="X" filename="{f}"><lines><line number="1" hits="1" branch="false" /></lines></class>"""))}
         </classes></package></packages></coverage>
         """;
    /// <summary>A workspace directory as a declared root: forward slashes, like the parser's keys.</summary>
    private static string RootOf(TempWorkspace ws, string relative) => ws.CreateDirectory(relative).Replace('\\', '/');
    [TestMethod]
    public void Parse_EmptySourceElement_DoesNotSwallowTheFollowingRoot()
    {
        var report = ParseXml(Doc("<sources><source/><source>/repo</source></sources>", "app/main.py"));
        Assert.AreEqual("/repo", report.SourceRoots.Single());
        Assert.AreEqual("/repo/app/main.py", report.Files.Single().Path);
    }

    [TestMethod]
    public void Parse_CommentInsideSource_IsNotARoot()
    {
        var report = ParseXml(Doc("<sources><source><!--ci checkout--></source></sources>", "app/main.py"));
        Assert.IsEmpty(report.SourceRoots);
        Assert.AreEqual("app/main.py", report.Files.Single().Path);
    }

    [TestMethod]
    public void Parse_MultipleRoots_WarningIsReportScopedWithEmptyFile()
    {
        // Probing a disk that holds neither candidate: the ambiguity concerns the report, not a
        // particular file or line.
        var report = ParseXml(Doc("<sources><source>/a</source><source>/b</source></sources>", "x.cs"), FileIdentity.ProbeSources);
        var w = Assert.ContainsSingle(report.Warnings);
        Assert.AreEqual(CoverageWarningKind.FileIdentityAmbiguous, w.Kind);
        Assert.AreEqual("", w.File);
        Assert.AreEqual(0, w.Line);
        Assert.Contains("'/a'", w.Detail);
    }

    [TestMethod]
    public void Parse_MultipleRoots_EachFileResolvesAgainstTheRootThatHasIt()
    {
        // coverage.py with `source = ["src", "tests"]` names each file relative to its own root.
        using var ws = TempWorkspace.Create("dotcov-source-roots-");
        var src = RootOf(ws, "src");
        var tests = RootOf(ws, "tests");
        ws.Write("src/pkg/app.py", "");
        ws.Write("tests/test_app.py", "");
        var report = ParseXml(Doc($"<sources><source>{src}</source><source>{tests}</source></sources>", "pkg/app.py", "test_app.py"), FileIdentity.ProbeSources);
        Assert.AreSequenceEqual(new[] { $"{src}/pkg/app.py", $"{tests}/test_app.py" }.Order(StringComparer.Ordinal), report.Files.Select(f => f.Path).Order(StringComparer.Ordinal), SequenceOrder.InAnyOrder);
        Assert.IsEmpty(report.Warnings);
        Assert.IsEmpty(report.AmbiguousFiles);
    }

    [TestMethod]
    public void Parse_MultipleRoots_FileUnderEveryRoot_UsesTheFirstAndWarns()
    {
        using var ws = TempWorkspace.Create("dotcov-source-roots-");
        var a = RootOf(ws, "a");
        var b = RootOf(ws, "b");
        ws.Write("a/x.py", "");
        ws.Write("b/x.py", "");
        var report = ParseXml(Doc($"<sources><source>{a}</source><source>{b}</source></sources>", "x.py"), FileIdentity.ProbeSources);
        Assert.AreEqual($"{a}/x.py", report.Files.Single().Path);
        var w = Assert.ContainsSingle(report.Warnings);
        Assert.AreEqual(CoverageWarningKind.FileIdentityAmbiguous, w.Kind);
        Assert.Contains($"'{a}'", w.Detail);
    }

    [TestMethod]
    public void Parse_NoOpRootBesideARealRoot_UsesTheRootThatHasTheFile()
    {
        using var ws = TempWorkspace.Create("dotcov-source-roots-");
        var src = RootOf(ws, "src");
        ws.Write("src/pkg/app.py", "");
        var report = ParseXml(Doc($"<sources><source>.</source><source>{src}</source></sources>", "pkg/app.py"), FileIdentity.ProbeSources);
        Assert.AreEqual($"{src}/pkg/app.py", report.Files.Single().Path);
        Assert.IsEmpty(report.Warnings);
    }
}
