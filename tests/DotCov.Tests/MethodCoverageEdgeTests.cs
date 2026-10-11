using System.Text;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

/// <summary>
/// The method-level model at its edges: attribute-less and empty <c>&lt;method&gt;</c> and
/// <c>&lt;class&gt;</c> elements, line ranges that include line zero, the default and
/// null-initialized <see cref="MethodCoverage.LineHits"/>, the shared
/// <see cref="MethodCoverageReport.Empty"/>, and input validation on the multi-input overload.
/// </summary>
public sealed class MethodCoverageEdgeTests
{
    private static MethodCoverageReport ParseMethods(string classes) =>
        CoberturaParser.ParseMethods(new MemoryStream(Encoding.UTF8.GetBytes(
            $"""
             <?xml version="1.0"?>
             <coverage><packages><package name="P"><classes>
             {classes}
             </classes></package></packages></coverage>
             """)));

    // ── Line range ────────────────────────────────────────────────────────────

    [Test]
    public async Task ParseMethods_MethodStartingAtLineZero_ReportsZeroAsItsStartLine()
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

        var m = await Assert.That(report.Methods).HasSingleItem();
        await Assert.That(m.StartLine).IsEqualTo(0);
        await Assert.That(m.EndLine).IsEqualTo(2);
        await Assert.That(m.LinesTotal).IsEqualTo(2);
        await Assert.That(m.LinesHit).IsEqualTo(1);
    }

    [Test]
    public async Task ParseMethods_LinesOutOfOrder_RangeIsLowestToHighest()
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

        var m = await Assert.That(report.Methods).HasSingleItem();
        await Assert.That(m.StartLine).IsEqualTo(9);
        await Assert.That(m.EndLine).IsEqualTo(15);
    }

    // ── Attribute-less and empty elements ─────────────────────────────────────

    [Test]
    public async Task ParseMethods_EmptyMethodElement_DoesNotCaptureTheClassSummaryLines()
    {
        // <method/> has no end tag; the class-level <lines> summary that follows belongs to no
        // method and must not be attributed to the empty one.
        var report = ParseMethods("""
            <class name="X" filename="src/A.cs">
              <methods><method name="Stub" signature="()" complexity="1" /></methods>
              <lines><line number="5" hits="3" /></lines>
            </class>
            """);

        var m = await Assert.That(report.Methods).HasSingleItem();
        await Assert.That(m.MethodName).IsEqualTo("Stub");
        await Assert.That(m.Complexity).IsEqualTo(1);
        await Assert.That(m.LinesTotal).IsEqualTo(0);
        await Assert.That(m.LineHits).IsEmpty();
        await Assert.That(m.StartLine).IsEqualTo(0);
        await Assert.That(m.EndLine).IsEqualTo(0);
    }

    [Test]
    public async Task ParseMethods_MissingNameAndSignatureAttributes_ReadAsEmptyStrings()
    {
        // An absent name or signature reads as the empty string (the value grcov writes
        // explicitly as signature=""); the entry still carries its lines and file key.
        var report = ParseMethods("""
            <class filename="src/A.cs"><methods>
              <method><lines><line number="4" hits="1" /></lines></method>
            </methods></class>
            """);

        var m = await Assert.That(report.Methods).HasSingleItem();
        await Assert.That(m.ClassName).IsEqualTo("");
        await Assert.That(m.MethodName).IsEqualTo("");
        await Assert.That(m.Signature).IsEqualTo("");
        await Assert.That(m.File).IsEqualTo("src/A.cs");
        await Assert.That(m.LineHits[4]).IsEqualTo(1);
    }

    // ── Public model surface ──────────────────────────────────────────────────

    [Test]
    public async Task MethodCoverage_DefaultAndNullInitialized_LineHitsAreEmpty()
    {
        // A default(MethodCoverage) never runs the property initializer, and an explicit null
        // init must not leak through the non-nullable surface either.
        var arr = new MethodCoverage[1];
        var built = new MethodCoverage("C", "M", "()", "a.cs", 1, 2, 1, 2, null) { LineHits = null! };

        await Assert.That(arr[0].LineHits).IsEmpty();
        await Assert.That(arr[0].LineRate).IsNull();
        await Assert.That(built.LineHits).IsEmpty();
        await Assert.That(built.LineRate).IsEqualTo(0.5);
    }

    [Test]
    public async Task MethodCoverage_ExplicitLineHits_AreReturnedAsGiven()
    {
        var hits = new Dictionary<int, int> { [1] = 3, [2] = 0 };
        var m = new MethodCoverage("C", "M", "()", "a.cs", 1, 2, 1, 2, 4) { LineHits = hits };

        await Assert.That(m.LineHits).IsSameReferenceAs(hits);
        await Assert.That(m.Complexity).IsEqualTo(4);
    }

    [Test]
    public async Task MethodCoverageReport_Empty_CarriesNothing()
    {
        var empty = MethodCoverageReport.Empty;

        await Assert.That(empty.Methods).IsEmpty();
        await Assert.That(empty.Warnings).IsEmpty();
        await Assert.That(empty.SourceRoots).IsEmpty();
        await Assert.That(MethodCoverageReport.Empty).IsSameReferenceAs(empty);
    }

    [Test]
    public async Task MethodCoverageReport_Constructor_ExposesItsArguments()
    {
        MethodCoverage[] methods = [new("C", "M", "()", "a.cs", 1, 1, 1, 1, 1)];
        CoverageWarning[] warnings = [new(CoverageWarningKind.MalformedHits, "a.cs", 1, "bad")];
        string[] roots = ["/repo"];

        var report = new MethodCoverageReport(methods, warnings, roots);

        await Assert.That(report.Methods).IsSameReferenceAs(methods);
        await Assert.That(report.Warnings).IsSameReferenceAs(warnings);
        await Assert.That(report.SourceRoots).IsSameReferenceAs(roots);
    }

    // ── Input validation ──────────────────────────────────────────────────────

    [Test]
    public async Task ParseMethods_NullInputInTheSet_ThrowsArgumentNullException_LikeParse()
    {
        var good = ReportInput.FromBytes("good", Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1)).ToBytes());
        ReportInput[] inputs = [good, null!];

        var parse = Assert.ThrowsExactly<ArgumentNullException>(() => CoberturaParser.Parse(inputs));
        var methods = Assert.ThrowsExactly<ArgumentNullException>(() => CoberturaParser.ParseMethods(inputs));

        await Assert.That(methods.ParamName).IsEqualTo(parse.ParamName);
    }

    [Test]
    public async Task ReportInput_ToString_IsTheSourceName()
    {
        using var ws = TempWorkspace.Create("dotcov-input-name-");
        var path = ws.Write("coverage.cobertura.xml", Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1)));

        await Assert.That(ReportInput.FromBytes("ci-job-7", [1]).ToString()).IsEqualTo("ci-job-7");
        await Assert.That(ReportInput.FromFile(path).ToString()).IsEqualTo(path);
        await Assert.That($"{ReportInput.FromFile(path)}").IsEqualTo(ReportInput.FromFile(path).SourceName);
    }
}
