using System.Text;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;
/// <summary>
/// The method-level model at its edges: attribute-less and empty <c>&lt;method&gt;</c> and
/// <c>&lt;class&gt;</c> elements, line ranges that include line zero, the default and
/// null-initialized <see cref = "MethodCoverage.LineHits"/>, the shared
/// <see cref = "MethodCoverageReport.Empty"/>, and input validation on the multi-input overload.
/// </summary>
[TestClass]
public sealed class MethodCoverageEdgeTests
{
    private static MethodCoverageReport ParseMethods(string classes)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes($"""
             <?xml version="1.0"?>
             <coverage><packages><package name="P"><classes>
             {classes}
             </classes></package></packages></coverage>
             """));
        return CoberturaParser.ParseMethods(stream);
    }
    // ── Line range ────────────────────────────────────────────────────────────
    [TestMethod]
    public void ParseMethods_MethodStartingAtLineZero_ReportsZeroAsItsStartLine()
    {
        // Line 0 is a valid line (ParserBranchTests attributes conditions to it), so a method on
        // lines 0..2 starts at 0, the lowest line number it covers.
        var report = ParseMethods("""
            <class name="X" filename="src/A.cs"><methods>
              <method name="M" signature="()"><lines>
                <line number="0" hits="1" />
                <line number="2" hits="0" />
              </lines></method>
            </methods></class>
            """);
        var m = Assert.ContainsSingle(report.Methods);
        Assert.AreEqual(0, m.StartLine);
        Assert.AreEqual(2, m.EndLine);
        Assert.AreEqual(2, m.LinesTotal);
        Assert.AreEqual(1, m.LinesHit);
    }

    [TestMethod]
    public void ParseMethods_LinesOutOfOrder_RangeIsLowestToHighest()
    {
        var report = ParseMethods("""
            <class name="X" filename="src/A.cs"><methods>
              <method name="M" signature="()"><lines>
                <line number="12" hits="1" />
                <line number="9" hits="1" />
                <line number="15" hits="1" />
                <line number="10" hits="1" />
              </lines></method>
            </methods></class>
            """);
        var m = Assert.ContainsSingle(report.Methods);
        Assert.AreEqual(9, m.StartLine);
        Assert.AreEqual(15, m.EndLine);
    }

    // ── Attribute-less and empty elements ─────────────────────────────────────
    [TestMethod]
    public void ParseMethods_EmptyMethodElement_DoesNotCaptureTheClassSummaryLines()
    {
        // <method/> has no end tag; the class-level <lines> summary that follows belongs to no
        // method and must not be attributed to the empty one.
        var report = ParseMethods("""
            <class name="X" filename="src/A.cs">
              <methods><method name="Stub" signature="()" complexity="1" /></methods>
              <lines><line number="5" hits="3" /></lines>
            </class>
            """);
        var m = Assert.ContainsSingle(report.Methods);
        Assert.AreEqual("Stub", m.MethodName);
        Assert.AreEqual(1, m.Complexity);
        Assert.AreEqual(0, m.LinesTotal);
        Assert.IsEmpty(m.LineHits);
        Assert.AreEqual(0, m.StartLine);
        Assert.AreEqual(0, m.EndLine);
    }

    [TestMethod]
    public void ParseMethods_MissingNameAndSignatureAttributes_ReadAsEmptyStrings()
    {
        // An absent name or signature reads as the empty string (the value grcov writes
        // explicitly as signature=""); the entry still carries its lines and file key.
        var report = ParseMethods("""
            <class filename="src/A.cs"><methods>
              <method><lines><line number="4" hits="1" /></lines></method>
            </methods></class>
            """);
        var m = Assert.ContainsSingle(report.Methods);
        Assert.AreEqual("", m.ClassName);
        Assert.AreEqual("", m.MethodName);
        Assert.AreEqual("", m.Signature);
        Assert.AreEqual("src/A.cs", m.File);
        Assert.AreEqual(1, m.LineHits[4]);
    }

    // ── Public model surface ──────────────────────────────────────────────────
    [TestMethod]
    public void MethodCoverage_DefaultAndNullInitialized_LineHitsAreEmpty()
    {
        // A default(MethodCoverage) never runs the property initializer, and an explicit null
        // init must not leak through the non-nullable surface either.
        var arr = new MethodCoverage[1];
        var built = new MethodCoverage("C", "M", "()", "a.cs", 1, 2, 1, 2, null)
        {
            LineHits = null!
        };
        Assert.IsEmpty(arr[0].LineHits);
        Assert.IsNull(arr[0].LineRate);
        Assert.IsEmpty(built.LineHits);
        Assert.AreEqual(0.5, built.LineRate);
    }

    [TestMethod]
    public void MethodCoverage_ExplicitLineHits_AreReturnedAsGiven()
    {
        var hits = new Dictionary<int, int>
        {
            [1] = 3,
            [2] = 0
        };
        var m = new MethodCoverage("C", "M", "()", "a.cs", 1, 2, 1, 2, 4)
        {
            LineHits = hits
        };
        Assert.AreSame(hits, m.LineHits);
        Assert.AreEqual(4, m.Complexity);
    }

    [TestMethod]
    public void MethodCoverageReport_Empty_CarriesNothing()
    {
        var empty = MethodCoverageReport.Empty;
        Assert.IsEmpty(empty.Methods);
        Assert.IsEmpty(empty.Warnings);
        Assert.IsEmpty(empty.SourceRoots);
        Assert.AreSame(empty, MethodCoverageReport.Empty);
    }

    [TestMethod]
    public void MethodCoverageReport_Constructor_ExposesItsArguments()
    {
        MethodCoverage[] methods = [new("C", "M", "()", "a.cs", 1, 1, 1, 1, 1)];
        CoverageWarning[] warnings = [new(CoverageWarningKind.MalformedHits, "a.cs", 1, "bad")];
        string[] roots = ["/repo"];
        var report = new MethodCoverageReport(methods, warnings, roots);
        Assert.AreSame(methods, report.Methods);
        // Warnings are recomputed for the methods held (ExternalSources), so they are equal, not the same list.
        Assert.AreSequenceEqual(warnings, report.Warnings, SequenceOrder.InAnyOrder);
        Assert.AreSame(roots, report.SourceRoots);
    }

    // ── Input validation ──────────────────────────────────────────────────────
    [TestMethod]
    public void ParseMethods_NullInputInTheSet_ThrowsArgumentNullException_LikeParse()
    {
        var good = ReportInput.FromBytes("good", Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1)).ToBytes());
        ReportInput[] inputs = [good, null!];
        var parse = Assert.ThrowsExactly<ArgumentNullException>(() => CoberturaParser.Parse(inputs));
        var methods = Assert.ThrowsExactly<ArgumentNullException>(() => CoberturaParser.ParseMethods(inputs));
        Assert.AreEqual(parse.ParamName, methods.ParamName);
    }

    [TestMethod]
    public void ReportInput_ToString_IsTheSourceName()
    {
        using var ws = TempWorkspace.Create("dotcov-input-name-");
        var path = ws.Write("coverage.cobertura.xml", Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1)));
        Assert.AreEqual("ci-job-7", ReportInput.FromBytes("ci-job-7", [1]).ToString());
        Assert.AreEqual(path, ReportInput.FromFile(path).ToString());
        Assert.AreEqual(ReportInput.FromFile(path).SourceName, $"{ReportInput.FromFile(path)}");
    }
}
