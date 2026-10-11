using System.Text;
using DotCov.Tests.Infrastructure;
using TUnit.Assertions.Enums;

namespace DotCov.Tests;

/// <summary>
/// Boundary input for the Cobertura parser: malformed or unusual nodes next to well-formed
/// ones, element-content shapes the XML spec allows (comments, processing instructions and
/// CDATA inside <c>&lt;source&gt;</c>), attribute values at the edge of their number range,
/// and the empty or attribute-less elements emitters write. Every document runs through the
/// synchronous and the asynchronous entry point, which must agree.
/// </summary>
public sealed class CoberturaParserEdgeTests
{
    private static string Doc(string sources, string classes) =>
        $"""
         <?xml version="1.0"?>
         <coverage>{sources}<packages><package name="P"><classes>
         {classes}
         </classes></package></packages></coverage>
         """;

    private static async Task<(CoverageReport Sync, CoverageReport Async)> ParseBoth(string xml)
    {
        var bytes = Encoding.UTF8.GetBytes(xml);
        var sync = CoberturaParser.Parse(new MemoryStream(bytes));
        using var stream = new MemoryStream(bytes);
        var async = await CoberturaParser.ParseAsync(stream);
        return (sync, async);
    }

    private static MethodCoverageReport ParseMethods(string xml) =>
        CoberturaParser.ParseMethods(new MemoryStream(Encoding.UTF8.GetBytes(xml)));

    // ── Conditions belong to their own line ──────────────────────────────────

    [Test]
    [Arguments("number=\"eleven\"")]
    [Arguments("")]
    public async Task Parse_ConditionsOfASkippedLine_DoNotAttachToThePreviousBranchedLine(string numberAttribute)
    {
        // A line whose number cannot be decoded is skipped, and so must be its nested
        // <condition>s. Attached to the previous branched line, they would turn line 10's honest
        // 1/2 into a fully covered 2/2 that vanishes from PartialBranches without a warning.
        var xml = Doc("", $"""
            <class name="X" filename="src/A.cs"><lines>
              <line number="10" hits="1" branch="true" condition-coverage="50% (1/2)">
                <conditions><condition number="0" type="jump" coverage="50%" /></conditions>
              </line>
              <line {numberAttribute} hits="1" branch="true" condition-coverage="100% (2/2)">
                <conditions><condition number="0" type="jump" coverage="100%" /></conditions>
              </line>
            </lines></class>
            """);

        var (sync, async) = await ParseBoth(xml);

        foreach (var report in new[] { sync, async })
        {
            var f = await Assert.That(report.Files).HasSingleItem();
            await Assert.That(f.LinesTotal).IsEqualTo(1);
            await Assert.That(f.BranchesByLine[10]).IsEqualTo((1, 2));
            await Assert.That(f.BranchesHit).IsEqualTo(1);
            await Assert.That(f.BranchesTotal).IsEqualTo(2);
            var partial = await Assert.That(f.PartialBranches).HasSingleItem();
            await Assert.That(partial).IsEqualTo(new BranchDetail(10, 1, 2));
            await Assert.That(f.ConditionsByLine[10][0]).IsEqualTo(1);
            await Assert.That(f.GetLineStatus(10)).IsEqualTo(LineStatus.Partial);
        }
    }

    [Test]
    public async Task ParseMethods_SkippedLineInsideAMethod_KeepsTheMethodsOtherLines()
    {
        // The method aggregation has no file accumulator to reset: the skipped line is simply
        // absent while its neighbours keep their hits.
        var methods = ParseMethods(Doc("", """
            <class name="X" filename="src/A.cs"><methods>
              <method name="M" signature="()"><lines>
                <line number="3" hits="2" branch="false" />
                <line number="four" hits="1" branch="true" condition-coverage="100% (2/2)">
                  <conditions><condition number="0" type="jump" coverage="100%" /></conditions>
                </line>
              </lines></method>
            </methods></class>
            """));

        var m = await Assert.That(methods.Methods).HasSingleItem();
        await Assert.That(m.LinesTotal).IsEqualTo(1);
        await Assert.That(m.LineHits[3]).IsEqualTo(2);
        await Assert.That(methods.Warnings).IsEmpty();
    }

    // ── condition-coverage must describe at most its total ───────────────────

    [Test]
    [Arguments("150% (3/2)")]
    [Arguments("(1/0)")]
    public async Task Parse_ConditionCoverageClaimingMoreCoveredThanTotal_IsDroppedWithAWarning(string condition)
    {
        // "(3/2)" cannot describe a line's branches. Taken verbatim, the file would report
        // BranchesHit 3 of BranchesTotal 2 — a 150% branch rate that clears any gate, the same
        // invariant the per-condition range gate protects.
        var xml = Doc("", $"""
            <class name="X" filename="src/A.cs"><lines>
              <line number="7" hits="1" branch="true" condition-coverage="{condition}" />
              <line number="8" hits="1" branch="true" condition-coverage="50% (1/2)" />
            </lines></class>
            """);

        var (sync, async) = await ParseBoth(xml);

        foreach (var report in new[] { sync, async })
        {
            var f = await Assert.That(report.Files).HasSingleItem();
            await Assert.That(f.BranchesByLine.ContainsKey(7)).IsFalse();
            await Assert.That(f.BranchesHit).IsEqualTo(1);
            await Assert.That(f.BranchesTotal).IsEqualTo(2);
            await Assert.That(report.BranchRate).IsEqualTo(0.5);
            var w = await Assert.That(report.Warnings).HasSingleItem();
            await Assert.That(w.Kind).IsEqualTo(CoverageWarningKind.MalformedConditionCoverage);
            await Assert.That(w.File).IsEqualTo("src/A.cs");
            await Assert.That(w.Line).IsEqualTo(7);
            await Assert.That(w.Detail).Contains(condition);
        }
    }

    [Test]
    public async Task Parse_FullyCoveredConditionCoverage_IsKept()
    {
        // The upper bound itself is legal: covered == total is a fully covered line.
        var (sync, async) = await ParseBoth(Doc("", """
            <class name="X" filename="src/A.cs"><lines>
              <line number="7" hits="1" branch="true" condition-coverage="100% (4/4)" />
            </lines></class>
            """));

        foreach (var report in new[] { sync, async })
        {
            await Assert.That(report.Files[0].BranchesByLine[7]).IsEqualTo((4, 4));
            await Assert.That(report.Warnings).IsEmpty();
        }
    }

    // ── <source> is the element's whole character content ────────────────────

    [Test]
    [Arguments("<source><!-- ci checkout -->/repo</source>")]
    [Arguments("<source><?emitter v1?>/repo</source>")]
    [Arguments("<source>/re<![CDATA[po]]></source>")]
    [Arguments("<source>/re<!-- split -->po</source>")]
    [Arguments("<source><![CDATA[/repo]]></source>")]
    public async Task Parse_SourceTextAroundCommentsPisAndCData_IsTheWholeRoot(string source)
    {
        // A comment, processing instruction or CDATA boundary does not change an element's
        // text. Reading only the first child node would drop the root after a leading comment
        // (every relative filename unprefixed) and truncate it to "/re" at a CDATA split — both
        // silently re-keying every file in the report.
        var xml = Doc($"<sources>{source}</sources>",
            """<class name="X" filename="app/main.py"><lines><line number="1" hits="1" /></lines></class>""");

        var (sync, async) = await ParseBoth(xml);

        foreach (var report in new[] { sync, async })
        {
            await Assert.That(report.SourceRoots).IsEquivalentTo(["/repo"], CollectionOrdering.Matching);
            var f = await Assert.That(report.Files).HasSingleItem();
            await Assert.That(f.Path).IsEqualTo("/repo/app/main.py");
            await Assert.That(report.Warnings).IsEmpty();
        }
    }

    [Test]
    [Arguments("<source/>")]
    [Arguments("<source></source>")]
    [Arguments("<source><!-- nothing --></source>")]
    [Arguments("<source><nested>/elsewhere</nested></source>")]
    public async Task Parse_SourceWithoutOwnText_DeclaresNoRootAndKeepsTheNextOne(string empty)
    {
        // No character content of its own means no root — and reading it must not swallow
        // the following <source>, which still roots the relative filename.
        var xml = Doc($"<sources>{empty}<source>/repo</source></sources>",
            """<class name="X" filename="app/main.py"><lines><line number="1" hits="1" /></lines></class>""");

        var (sync, async) = await ParseBoth(xml);

        foreach (var report in new[] { sync, async })
        {
            await Assert.That(report.SourceRoots).IsEquivalentTo(["/repo"], CollectionOrdering.Matching);
            await Assert.That(report.Files.Single().Path).IsEqualTo("/repo/app/main.py");
            await Assert.That(report.Warnings).IsEmpty();
        }
    }

    // ── Empty and attribute-less elements ────────────────────────────────────

    [Test]
    public async Task Parse_ClassWithoutFilename_IsSkippedOnBothPaths()
    {
        // No filename, nothing to attribute: the class and its lines are dropped, while the
        // attributable class next to it is unaffected.
        var (sync, async) = await ParseBoth(Doc("", """
            <class name="Anon"><lines><line number="1" hits="1" /></lines></class>
            <class name="X" filename="src/A.cs"><lines><line number="2" hits="0" /></lines></class>
            """));

        foreach (var report in new[] { sync, async })
        {
            var f = await Assert.That(report.Files).HasSingleItem();
            await Assert.That(f.Path).IsEqualTo("src/A.cs");
            await Assert.That(f.LinesTotal).IsEqualTo(1);
            await Assert.That(f.LinesHit).IsEqualTo(0);
        }
    }

    [Test]
    public async Task Parse_ClassWithoutNameAttribute_StillContributesItsFile()
    {
        // The class name only identifies methods; the file aggregation keys on filename alone.
        var (sync, async) = await ParseBoth(Doc("", """
            <class filename="src/A.cs"><lines><line number="1" hits="1" /><line number="2" hits="0" /></lines></class>
            """));

        foreach (var report in new[] { sync, async })
        {
            var f = await Assert.That(report.Files).HasSingleItem();
            await Assert.That(f.Path).IsEqualTo("src/A.cs");
            await Assert.That(f.LinesTotal).IsEqualTo(2);
            await Assert.That(f.LinesHit).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Parse_EmptyClassElement_IsAFileWithoutLines_AndDoesNotSwallowItsSibling()
    {
        // <class .../> has no end tag. It still names a file (measured as nothing), and the
        // next class is read in full on both paths.
        var (sync, async) = await ParseBoth(Doc("", """
            <class name="E" filename="src/Empty.cs" />
            <class name="X" filename="src/A.cs"><lines><line number="1" hits="1" /></lines></class>
            """));

        foreach (var report in new[] { sync, async })
        {
            await Assert.That(report.Files.Count).IsEqualTo(2);
            var empty = report.Files.Single(static f => f.Path == "src/Empty.cs");
            await Assert.That(empty.LinesTotal).IsEqualTo(0);
            await Assert.That(empty.LineRate).IsNull();
            var a = report.Files.Single(static f => f.Path == "src/A.cs");
            await Assert.That(a.LinesHit).IsEqualTo(1);
        }
    }

    [Test]
    public async Task Parse_CommentsAndTextInsideAClass_AreIgnored()
    {
        // Only elements carry coverage; a comment or stray text between them changes nothing.
        var (sync, async) = await ParseBoth(Doc("", """
            <class name="X" filename="src/A.cs"><!-- emitted by hand --><lines>stray<line number="1" hits="1" /><!-- end --></lines></class>
            """));

        foreach (var report in new[] { sync, async })
        {
            var f = await Assert.That(report.Files).HasSingleItem();
            await Assert.That(f.LinesTotal).IsEqualTo(1);
            await Assert.That(f.LinesHit).IsEqualTo(1);
            await Assert.That(report.Warnings).IsEmpty();
        }
    }

    [Test]
    public async Task Parse_DriveRelativeFilename_IsRootedAndKeepsItsSpelling()
    {
        // "c:App.cs" carries a drive, so no <source> root is prepended. Only the "x:/" form is
        // drive-letter-normalized — the same rule PathIdentity.NormalizeRoot applies to roots.
        var (sync, async) = await ParseBoth(Doc("<sources><source>/repo</source></sources>", """
            <class name="X" filename="c:App.cs"><lines><line number="1" hits="1" /></lines></class>
            <class name="Y" filename="d:\w\B.cs"><lines><line number="1" hits="1" /></lines></class>
            """));

        foreach (var report in new[] { sync, async })
        {
            await Assert.That(report.Files.Select(static f => f.Path))
                .IsEquivalentTo(["c:App.cs", "D:/w/B.cs"], CollectionOrdering.Matching);
        }
    }

    // ── Culture and encoding ─────────────────────────────────────────────────

    [Test]
    [NotInParallel(ProcessState.Environment)]
    public async Task Parse_UnderACommaDecimalCulture_ReadsDotDecimalAttributes()
    {
        // Every numeric attribute is invariant: "1.6" complexity and a "50.0%" condition keep
        // their meaning on a host whose decimal separator is ','.
        var bytes = Encoding.UTF8.GetBytes(Doc("", """
            <class name="X" filename="src/A.cs">
              <methods><method name="M" signature="()" complexity="1.6"><lines><line number="1" hits="1" /></lines></method></methods>
              <lines>
                <line number="1" hits="1" />
                <line number="2" hits="1" branch="true" condition-coverage="50.0% (1/2)">
                  <conditions><condition number="0" type="jump" coverage="50.0%" /></conditions>
                </line>
              </lines>
            </class>
            """));

        CoverageReport report;
        MethodCoverageReport methods;
        using (CultureScope.CommaDecimal())
        {
            report = CoberturaParser.Parse(new MemoryStream(bytes));
            methods = CoberturaParser.ParseMethods(new MemoryStream(bytes));
        }

        await Assert.That(methods.Methods.Single().Complexity).IsEqualTo(2);
        var f = await Assert.That(report.Files).HasSingleItem();
        await Assert.That(f.BranchesByLine[2]).IsEqualTo((1, 2));
        await Assert.That(f.ConditionsByLine[2][0]).IsEqualTo(1);
        await Assert.That(report.Warnings).IsEmpty();
    }

    [Test]
    public async Task Parse_Utf16DocumentWithByteOrderMark_ReadsLikeUtf8OnBothPaths()
    {
        // The reader honours the BOM and the declaration: a UTF-16 report keeps its non-ASCII
        // root and filename intact on the sync and the async path.
        var xml = Doc("<sources><source>/répo</source></sources>",
            """<class name="X" filename="src/Größe.cs"><lines><line number="1" hits="2" /></lines></class>""")
            .Replace("<?xml version=\"1.0\"?>", "<?xml version=\"1.0\" encoding=\"utf-16\"?>");
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(xml)).ToArray();

        var sync = CoberturaParser.Parse(new MemoryStream(bytes));
        using var stream = new MemoryStream(bytes);
        var async = await CoberturaParser.ParseAsync(stream);

        foreach (var report in new[] { sync, async })
        {
            var f = await Assert.That(report.Files).HasSingleItem();
            await Assert.That(f.Path).IsEqualTo("/répo/src/Größe.cs");
            await Assert.That(f.LineHits[1]).IsEqualTo(2);
        }
    }

    // ── Hit counts beyond Int64 ──────────────────────────────────────────────

    [Test]
    [Arguments("9223372036854775808", int.MaxValue, 1)]
    [Arguments("18446744073709551615", int.MaxValue, 1)]
    [Arguments("-9223372036854775809", int.MinValue, 0)]
    public async Task Parse_HitsBeyondTheInt64Range_SaturateInsteadOfDegradingToAMiss(string hits, int expected, int linesHit)
    {
        // Counts above int.MaxValue saturate (TryDecodeLine's contract: "only >0 matters
        // downstream"), even past the Int64 range, where a failed long parse must not raise
        // MalformedHits and count the executed line as a miss.
        var xml = Doc("", $"""
            <class name="X" filename="src/A.cs"><lines>
              <line number="1" hits="{hits}" branch="false" />
            </lines></class>
            """);

        var (sync, async) = await ParseBoth(xml);

        foreach (var report in new[] { sync, async })
        {
            var f = await Assert.That(report.Files).HasSingleItem();
            await Assert.That(f.LineHits[1]).IsEqualTo(expected);
            await Assert.That(f.LinesHit).IsEqualTo(linesHit);
            await Assert.That(report.Warnings).IsEmpty();
        }
    }

    [Test]
    public async Task Parse_HitsThatAreNotAnInteger_StillWarnAndCountAsZero()
    {
        // The saturating fallback accepts only integers: a decimal or exponent stays malformed.
        var (sync, async) = await ParseBoth(Doc("", """
            <class name="X" filename="src/A.cs"><lines>
              <line number="1" hits="1e30" branch="false" />
            </lines></class>
            """));

        foreach (var report in new[] { sync, async })
        {
            await Assert.That(report.Files[0].LineHits[1]).IsEqualTo(0);
            var w = await Assert.That(report.Warnings).HasSingleItem();
            await Assert.That(w.Kind).IsEqualTo(CoverageWarningKind.MalformedHits);
            await Assert.That(w.Detail).Contains("1e30");
        }
    }
}
