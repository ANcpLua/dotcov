using System.Text;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;
/// <summary>
/// Boundary input for the Cobertura parser: malformed or unusual nodes next to well-formed
/// ones, element-content shapes the XML spec allows (comments, processing instructions and
/// CDATA inside <c>&lt;source&gt;</c>), attribute values at the edge of their number range,
/// and the empty or attribute-less elements emitters write. Every document runs through the
/// synchronous and the asynchronous entry point, which must agree.
/// </summary>
[TestClass]
public sealed class CoberturaParserEdgeTests(TestContext testContext)
{
    private static string Doc(string sources, string classes) => $"""
         <?xml version="1.0"?>
         <coverage>{sources}<packages><package name="P"><classes>
         {classes}
         </classes></package></packages></coverage>
         """;
    private async Task<(CoverageReport Sync, CoverageReport Async)> ParseBoth(string xml)
    {
        var bytes = Encoding.UTF8.GetBytes(xml);
        using var syncStream = new MemoryStream(bytes);
        // Exercise the synchronous parser contract alongside awaited assertions.
        // ReSharper disable once MethodHasAsyncOverload
        var sync = CoberturaParser.Parse(syncStream);
        using var stream = new MemoryStream(bytes);
        var async = await CoberturaParser.ParseAsync(stream, ct: testContext.CancellationToken);
        return (sync, async);
    }

    private static MethodCoverageReport ParseMethods(string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return CoberturaParser.ParseMethods(stream);
    }
    // ── Conditions belong to their own line ──────────────────────────────────
    [TestMethod]
    [DataRow("number=\"eleven\"")]
    [DataRow("")]
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
        var(sync, async) = await ParseBoth(xml);
        foreach (var report in new[]
        {
            sync,
            async
        }

        )
        {
            var f = Assert.ContainsSingle(report.Files);
            Assert.AreEqual(1, f.LinesTotal);
            Assert.AreEqual((1, 2), f.BranchesByLine[10]);
            Assert.AreEqual(1, f.BranchesHit);
            Assert.AreEqual(2, f.BranchesTotal);
            var partial = Assert.ContainsSingle(f.PartialBranches);
            Assert.AreEqual(new BranchDetail(10, 1, 2), partial);
            Assert.AreEqual(1, f.ConditionsByLine[10][0]);
            Assert.AreEqual(LineStatus.Partial, f.GetLineStatus(10));
        }
    }

    [TestMethod]
    public void ParseMethods_SkippedLineInsideAMethod_KeepsTheMethodsOtherLines()
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
        var m = Assert.ContainsSingle(methods.Methods);
        Assert.AreEqual(1, m.LinesTotal);
        Assert.AreEqual(2, m.LineHits[3]);
        Assert.IsEmpty(methods.Warnings);
    }

    // ── condition-coverage must describe at most its total ───────────────────
    [TestMethod]
    [DataRow("150% (3/2)")]
    [DataRow("(1/0)")]
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
        var(sync, async) = await ParseBoth(xml);
        foreach (var report in new[]
        {
            sync,
            async
        }

        )
        {
            var f = Assert.ContainsSingle(report.Files);
            Assert.DoesNotContain(7, f.BranchesByLine.Keys);
            Assert.AreEqual(1, f.BranchesHit);
            Assert.AreEqual(2, f.BranchesTotal);
            Assert.AreEqual(0.5, report.BranchRate);
            var w = Assert.ContainsSingle(report.Warnings);
            Assert.AreEqual(CoverageWarningKind.MalformedConditionCoverage, w.Kind);
            Assert.AreEqual("src/A.cs", w.File);
            Assert.AreEqual(7, w.Line);
            Assert.Contains(condition, w.Detail);
        }
    }

    [TestMethod]
    public async Task Parse_FullyCoveredConditionCoverage_IsKept()
    {
        // The upper bound itself is legal: covered == total is a fully covered line.
        var(sync, async) = await ParseBoth(Doc("", """
            <class name="X" filename="src/A.cs"><lines>
              <line number="7" hits="1" branch="true" condition-coverage="100% (4/4)" />
            </lines></class>
            """));
        foreach (var report in new[]
        {
            sync,
            async
        }

        )
        {
            Assert.AreEqual((4, 4), report.Files[0].BranchesByLine[7]);
            Assert.IsEmpty(report.Warnings);
        }
    }

    // ── <source> is the element's whole character content ────────────────────
    [TestMethod]
    [DataRow("<source><!-- ci checkout -->/repo</source>")]
    [DataRow("<source><?emitter v1?>/repo</source>")]
    [DataRow("<source>/re<![CDATA[po]]></source>")]
    [DataRow("<source>/re<!-- split -->po</source>")]
    [DataRow("<source><![CDATA[/repo]]></source>")]
    public async Task Parse_SourceTextAroundCommentsPisAndCData_IsTheWholeRoot(string source)
    {
        // A comment, processing instruction or CDATA boundary does not change an element's
        // text. Reading only the first child node would drop the root after a leading comment
        // (every relative filename unprefixed) and truncate it to "/re" at a CDATA split — both
        // silently re-keying every file in the report.
        var xml = Doc($"<sources>{source}</sources>", """<class name="X" filename="app/main.py"><lines><line number="1" hits="1" /></lines></class>""");
        var(sync, async) = await ParseBoth(xml);
        foreach (var report in new[]
        {
            sync,
            async
        }

        )
        {
            Assert.AreSequenceEqual(["/repo"], report.SourceRoots, SequenceOrder.InOrder);
            var f = Assert.ContainsSingle(report.Files);
            Assert.AreEqual("/repo/app/main.py", f.Path);
            Assert.IsEmpty(report.Warnings);
        }
    }

    [TestMethod]
    [DataRow("<source/>")]
    [DataRow("<source></source>")]
    [DataRow("<source><!-- nothing --></source>")]
    [DataRow("<source><nested>/elsewhere</nested></source>")]
    public async Task Parse_SourceWithoutOwnText_DeclaresNoRootAndKeepsTheNextOne(string empty)
    {
        // No character content of its own means no root — and reading it must not swallow
        // the following <source>, which still roots the relative filename.
        var xml = Doc($"<sources>{empty}<source>/repo</source></sources>", """<class name="X" filename="app/main.py"><lines><line number="1" hits="1" /></lines></class>""");
        var(sync, async) = await ParseBoth(xml);
        foreach (var report in new[]
        {
            sync,
            async
        }

        )
        {
            Assert.AreSequenceEqual(["/repo"], report.SourceRoots, SequenceOrder.InOrder);
            Assert.AreEqual("/repo/app/main.py", Assert.ContainsSingle(report.Files).Path);
            Assert.IsEmpty(report.Warnings);
        }
    }

    // ── Empty and attribute-less elements ────────────────────────────────────
    [TestMethod]
    public async Task Parse_ClassWithoutFilename_IsSkippedOnBothPaths()
    {
        // No filename, nothing to attribute: the class and its lines are dropped, while the
        // attributable class next to it is unaffected.
        var(sync, async) = await ParseBoth(Doc("", """
            <class name="Anon"><lines><line number="1" hits="1" /></lines></class>
            <class name="X" filename="src/A.cs"><lines><line number="2" hits="0" /></lines></class>
            """));
        foreach (var report in new[]
        {
            sync,
            async
        }

        )
        {
            var f = Assert.ContainsSingle(report.Files);
            Assert.AreEqual("src/A.cs", f.Path);
            Assert.AreEqual(1, f.LinesTotal);
            Assert.AreEqual(0, f.LinesHit);
        }
    }

    [TestMethod]
    public async Task Parse_ClassWithoutNameAttribute_StillContributesItsFile()
    {
        // The class name only identifies methods; the file aggregation keys on filename alone.
        var(sync, async) = await ParseBoth(Doc("", """
            <class filename="src/A.cs"><lines><line number="1" hits="1" /><line number="2" hits="0" /></lines></class>
            """));
        foreach (var report in new[]
        {
            sync,
            async
        }

        )
        {
            var f = Assert.ContainsSingle(report.Files);
            Assert.AreEqual("src/A.cs", f.Path);
            Assert.AreEqual(2, f.LinesTotal);
            Assert.AreEqual(1, f.LinesHit);
        }
    }

    [TestMethod]
    public async Task Parse_EmptyClassElement_IsAFileWithoutLines_AndDoesNotSwallowItsSibling()
    {
        // <class .../> has no end tag. It still names a file (measured as nothing), and the
        // next class is read in full on both paths.
        var(sync, async) = await ParseBoth(Doc("", """
            <class name="E" filename="src/Empty.cs" />
            <class name="X" filename="src/A.cs"><lines><line number="1" hits="1" /></lines></class>
            """));
        foreach (var report in new[]
        {
            sync,
            async
        }

        )
        {
            Assert.HasCount(2, report.Files);
            var empty = Assert.ContainsSingle(static f => f.Path == "src/Empty.cs", report.Files);
            Assert.AreEqual(0, empty.LinesTotal);
            Assert.IsNull(empty.LineRate);
            var a = Assert.ContainsSingle(static f => f.Path == "src/A.cs", report.Files);
            Assert.AreEqual(1, a.LinesHit);
        }
    }

    [TestMethod]
    public async Task Parse_CommentsAndTextInsideAClass_AreIgnored()
    {
        // Only elements carry coverage; a comment or stray text between them changes nothing.
        var(sync, async) = await ParseBoth(Doc("", """
            <class name="X" filename="src/A.cs"><!-- emitted by hand --><lines>stray<line number="1" hits="1" /><!-- end --></lines></class>
            """));
        foreach (var report in new[]
        {
            sync,
            async
        }

        )
        {
            var f = Assert.ContainsSingle(report.Files);
            Assert.AreEqual(1, f.LinesTotal);
            Assert.AreEqual(1, f.LinesHit);
            Assert.IsEmpty(report.Warnings);
        }
    }

    [TestMethod]
    public async Task Parse_DriveRelativeFilename_IsRootedAndKeepsItsSpelling()
    {
        // "c:App.cs" carries a drive, so no <source> root is prepended. Only the "x:/" form is
        // drive-letter-normalized — the same rule PathIdentity.NormalizeRoot applies to roots.
        var(sync, async) = await ParseBoth(Doc("<sources><source>/repo</source></sources>", """
            <class name="X" filename="c:App.cs"><lines><line number="1" hits="1" /></lines></class>
            <class name="Y" filename="d:\w\B.cs"><lines><line number="1" hits="1" /></lines></class>
            """));
        foreach (var report in new[]
        {
            sync,
            async
        }

        )
        {
            Assert.AreSequenceEqual(["c:App.cs", "D:/w/B.cs"], report.Files.Select(static f => f.Path), SequenceOrder.InOrder);
        }
    }

    // ── Culture and encoding ─────────────────────────────────────────────────
    [TestMethod]
    [ResourceLock(ProcessState.Environment)]
    public void Parse_UnderACommaDecimalCulture_ReadsDotDecimalAttributes()
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
            using var reportStream = new MemoryStream(bytes);
            using var methodsStream = new MemoryStream(bytes);
            // Exercise the synchronous parser contract alongside awaited assertions.
            // ReSharper disable once MethodHasAsyncOverload
            report = CoberturaParser.Parse(reportStream);
            methods = CoberturaParser.ParseMethods(methodsStream);
        }

        Assert.AreEqual(2, Assert.ContainsSingle(methods.Methods).Complexity);
        var f = Assert.ContainsSingle(report.Files);
        Assert.AreEqual((1, 2), f.BranchesByLine[2]);
        Assert.AreEqual(1, f.ConditionsByLine[2][0]);
        Assert.IsEmpty(report.Warnings);
    }

    [TestMethod]
    public async Task Parse_Utf16DocumentWithByteOrderMark_ReadsLikeUtf8OnBothPaths()
    {
        // The reader honours the BOM and the declaration: a UTF-16 report keeps its non-ASCII
        // root and filename intact on the sync and the async path.
        var xml = Doc("<sources><source>/répo</source></sources>", """<class name="X" filename="src/Größe.cs"><lines><line number="1" hits="2" /></lines></class>""").Replace("<?xml version=\"1.0\"?>", "<?xml version=\"1.0\" encoding=\"utf-16\"?>");
        var bytes = Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes(xml)).ToArray();
        using var syncStream = new MemoryStream(bytes);
        // Exercise the synchronous parser contract alongside awaited assertions.
        // ReSharper disable once MethodHasAsyncOverload
        var sync = CoberturaParser.Parse(syncStream);
        using var stream = new MemoryStream(bytes);
        var async = await CoberturaParser.ParseAsync(stream, ct: testContext.CancellationToken);
        foreach (var report in new[]
        {
            sync,
            async
        }

        )
        {
            var f = Assert.ContainsSingle(report.Files);
            Assert.AreEqual("/répo/src/Größe.cs", f.Path);
            Assert.AreEqual(2, f.LineHits[1]);
        }
    }

    // ── Hit counts beyond Int64 ──────────────────────────────────────────────
    [TestMethod]
    [DataRow("9223372036854775808", int.MaxValue, 1)]
    [DataRow("18446744073709551615", int.MaxValue, 1)]
    [DataRow("-9223372036854775809", int.MinValue, 0)]
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
        var(sync, async) = await ParseBoth(xml);
        foreach (var report in new[]
        {
            sync,
            async
        }

        )
        {
            var f = Assert.ContainsSingle(report.Files);
            Assert.AreEqual(expected, f.LineHits[1]);
            Assert.AreEqual(linesHit, f.LinesHit);
            Assert.IsEmpty(report.Warnings);
        }
    }

    [TestMethod]
    public async Task Parse_HitsThatAreNotAnInteger_StillWarnAndCountAsZero()
    {
        // The saturating fallback accepts only integers: a decimal or exponent stays malformed.
        var(sync, async) = await ParseBoth(Doc("", """
            <class name="X" filename="src/A.cs"><lines>
              <line number="1" hits="1e30" branch="false" />
            </lines></class>
            """));
        foreach (var report in new[]
        {
            sync,
            async
        }

        )
        {
            Assert.AreEqual(0, report.Files[0].LineHits[1]);
            var w = Assert.ContainsSingle(report.Warnings);
            Assert.AreEqual(CoverageWarningKind.MalformedHits, w.Kind);
            Assert.Contains("1e30", w.Detail);
        }
    }
}
