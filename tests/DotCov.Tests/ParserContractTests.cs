using System.Text.Json.Nodes;
using System.Xml;
using DotCov.Formatters;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;
/// <summary>
/// One document, four ways in — caller-owned stream (sync and async), in-memory input, file
/// input — must yield the same report. Also the responsibilities that sit at the input
/// boundary: multi-input merging, who disposes which stream, structured error coordinates,
/// and the per-document character cap.
/// </summary>
[TestClass]
public sealed class ParserContractTests : IDisposable
{
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-contract-");
    public void Dispose() => _ws.Dispose();
    public static IEnumerable<(string Name, Cobertura Document)> Documents()
    {
        yield return ("two plain classes", Cobertura.NewDoc().AddClass("src/A.cs", c => c.Line(1, hits: 3).Line(2, hits: 0)).AddClass("src/B.cs", c => c.Line(5, hits: 1)));
        yield return ("coverlet layout with methods, branches and conditions", Cobertura.NewDoc().WithSource("/repo").AddClass("src/A.cs", "MyApp.A", c => c.Method("M", "()", "2", m => m.Line(1, hits: 1).Line(2, hits: 0)).Line(1, hits: 1).Line(2, hits: 0).BranchWithConditions(3, "50% (1/2)", (0, "50%"))));
        yield return ("same file under two class blocks", Cobertura.NewDoc().AddClass("src/A.cs", "MyApp.A", c => c.Line(1, hits: 0).Line(2, hits: 1)).AddClass("src/A.cs", "MyApp.A/<M>d__1", c => c.Line(1, hits: 2).Line(3, hits: 0)));
        yield return ("malformed hits and condition strings warn", Cobertura.NewDoc().AddClass("src/A.cs", "MyApp.A", c => c.Method("M", "()", "1", m => m.MalformedLine("7", "lots")).MalformedLine("7", "lots").Branch(8, "garbage")));
        yield return ("two source roots and a windows path", Cobertura.NewDoc().WithSource("c:\\work\\repo").WithSource("/other").AddClass("src\\Win.cs", c => c.Line(1, hits: 1)));
    }

    // Inputs name where a document came from, so they differ by source by design; each source's
    // are pinned on their own.
    private static string Canonical(CoverageReport report)
    {
        var json = JsonNode.Parse(JsonFormatter.Format(report))!.AsObject();
        json.Remove("inputs");
        return json.ToJsonString() + "\n" + string.Join("|", report.SourceRoots);
    }

    private static IEnumerable<string> Canonical(MethodCoverageReport report) => report.Methods.Select(m => $"{m.File}|{m.ClassName}|{m.MethodName}|{m.Signature}|{m.StartLine}-{m.EndLine}|{m.LinesHit}/{m.LinesTotal}|{m.Complexity}|" + string.Join(",", m.LineHits.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"))).Concat(report.Warnings.Select(w => $"warning:{w.Kind}:{w.File}:{w.Line}:{w.Detail}")).Concat(report.SourceRoots.Select(r => $"root:{r}"));
    // ── Same document, every source ───────────────────────────────────────────
    [TestMethod]
    [DynamicData(nameof(Documents))]
    public async Task Parse_MemoryAndFileAndStreamSources_YieldTheSameReport(string name, Cobertura document)
    {
        var bytes = document.ToBytes();
        var path = _ws.Write($"{name.GetHashCode():x}/coverage.cobertura.xml", bytes);
        using var syncStream = new MemoryStream(bytes);
        using var asyncStream = new MemoryStream(bytes);
        // Exercise the synchronous parser contract alongside awaited assertions.
        // ReSharper disable once MethodHasAsyncOverload
        var viaStream = CoberturaParser.Parse(syncStream);
        var viaAsync = await CoberturaParser.ParseAsync(asyncStream);
        var viaBytes = CoberturaParser.Parse(ReportInput.FromBytes(name, bytes));
        var viaFile = CoberturaParser.Parse(ReportInput.FromFile(path));
        var viaResolver = CoberturaParser.Parse(ReportResolver.Resolve(path));
        var expected = Canonical(viaStream);
        Assert.AreEqual(expected, Canonical(viaAsync), message: $"{name}: async");
        Assert.AreEqual(expected, Canonical(viaBytes), message: $"{name}: bytes");
        Assert.AreEqual(expected, Canonical(viaFile), message: $"{name}: file");
        Assert.AreEqual(expected, Canonical(viaResolver), message: $"{name}: resolver");
        Assert.AreEqual(viaFile.Warnings.Count, viaStream.Warnings.Count);
        // A bare stream has no name; every ReportInput is named by its source.
        Assert.IsEmpty(viaStream.Inputs);
        Assert.IsEmpty(viaAsync.Inputs);
        Assert.AreSequenceEqual([name], viaBytes.Inputs, SequenceOrder.InOrder);
        Assert.AreSequenceEqual([path], viaFile.Inputs, SequenceOrder.InOrder);
        Assert.AreSequenceEqual([path], viaResolver.Inputs, SequenceOrder.InOrder);
    }

    [TestMethod]
    [DynamicData(nameof(Documents))]
    public void ParseMethods_MemoryAndFileAndStreamSources_YieldTheSameReport(string name, Cobertura document)
    {
        var bytes = document.ToBytes();
        var path = _ws.Write($"m-{name.GetHashCode():x}/coverage.cobertura.xml", bytes);
        using var stream = new MemoryStream(bytes);
        var viaStream = CoberturaParser.ParseMethods(stream);
        var viaBytes = CoberturaParser.ParseMethods(ReportInput.FromBytes(name, bytes));
        var viaFile = CoberturaParser.ParseMethods(ReportInput.FromFile(path));
        var viaResolver = CoberturaParser.ParseMethods(ReportResolver.Resolve(path));
        var expected = Canonical(viaStream).ToList();
        Assert.AreSequenceEqual(expected, Canonical(viaBytes), SequenceOrder.InOrder, message: $"{name}: bytes");
        Assert.AreSequenceEqual(expected, Canonical(viaFile), SequenceOrder.InOrder, message: $"{name}: file");
        Assert.AreSequenceEqual(expected, Canonical(viaResolver), SequenceOrder.InOrder, message: $"{name}: resolver");
        Assert.IsEmpty(viaStream.Inputs);
        Assert.AreSequenceEqual([name], viaBytes.Inputs, SequenceOrder.InOrder);
        Assert.AreSequenceEqual([path], viaFile.Inputs, SequenceOrder.InOrder);
        Assert.AreSequenceEqual([path], viaResolver.Inputs, SequenceOrder.InOrder);
    }

    [TestMethod]
    public void Parse_WarningsAreIdenticalOnFileAndMethodPaths()
    {
        // The shared line decoder raises MalformedHits once per malformed <line>; both
        // aggregations observe the same document, so both carry the warning.
        var bytes = Cobertura.NewDoc().AddClass("src/A.cs", "MyApp.A", c => c.Method("M", "()", "1", m => m.MalformedLine("7", "lots"))).ToBytes();
        var files = CoberturaParser.Parse(ReportInput.FromBytes("doc", bytes));
        var methods = CoberturaParser.ParseMethods(ReportInput.FromBytes("doc", bytes));
        var w = Assert.ContainsSingle(files.Warnings);
        Assert.AreEqual(CoverageWarningKind.MalformedHits, w.Kind);
        Assert.AreSequenceEqual(files.Warnings, methods.Warnings, SequenceOrder.InOrder);
    }

    // ── Multiple inputs ───────────────────────────────────────────────────────
    [TestMethod]
    public void Parse_MultipleInputs_MergesInInputOrder()
    {
        var first = ReportInput.FromBytes("r1", Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1)).ToBytes());
        var second = ReportInput.FromBytes("r2", Cobertura.NewDoc().AddClass("a.cs", c => c.Line(2, 1).Line(3, 0)).ToBytes());
        var third = ReportInput.FromBytes("r3", Cobertura.NewDoc().AddClass("b.cs", c => c.Line(1, 1)).ToBytes());
        var report = CoberturaParser.Parse([first, second, third]);
        Assert.AreEqual(2, report.Files.Count);
        var a = report.Files.Single(f => f.Path == "a.cs");
        Assert.AreEqual(3, a.LinesTotal);
        Assert.AreEqual(2, a.LinesHit);
        Assert.AreEqual("a.cs", report.Files[0].Path); // first-seen order survives the merge
    }

    [TestMethod]
    public void Parse_NoInputs_IsAnEmptyReport_NotAnError()
    {
        var report = CoberturaParser.Parse(Array.Empty<ReportInput>());
        var methods = CoberturaParser.ParseMethods(Array.Empty<ReportInput>());
        Assert.IsEmpty(report.Files);
        Assert.AreEqual(GateOutcome.NoData, report.Evaluate(80).Outcome);
        Assert.IsEmpty(methods.Methods);
        Assert.IsEmpty(methods.Warnings);
    }

    // ── Stream ownership ──────────────────────────────────────────────────────
    [TestMethod]
    public async Task Parse_CallerStream_IsLeftOpen()
    {
        var bytes = Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1)).ToBytes();
        using var sync = new MemoryStream(bytes);
        using var async = new MemoryStream(bytes);
        using var methods = new MemoryStream(bytes);
        // Exercise the synchronous parser contract alongside awaited assertions.
        // ReSharper disable once MethodHasAsyncOverload
        CoberturaParser.Parse(sync);
        await CoberturaParser.ParseAsync(async);
        CoberturaParser.ParseMethods(methods);
        Assert.IsTrue(sync.CanRead);
        Assert.IsTrue(async.CanRead);
        Assert.IsTrue(methods.CanRead);
    }

    [TestMethod]
    public async Task Parse_InputStream_IsClosedAfterSuccessAndAfterFailure()
    {
        // Opening the file exclusively afterwards proves the parser released its handle. On
        // Unix the runtime maps FileShare.None to an advisory lock, so a leaked reader would
        // make the exclusive open throw.
        var good = _ws.Write("good/coverage.cobertura.xml", Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1)));
        var bad = _ws.Write("bad/coverage.cobertura.xml", "<coverage><packa");
        CoberturaParser.Parse(ReportInput.FromFile(good));
        CoberturaParser.ParseMethods(ReportInput.FromFile(good));
        Assert.ThrowsExactly<ReportParseException>(() => CoberturaParser.Parse(ReportInput.FromFile(bad)));
        Assert.ThrowsExactly<ReportParseException>(() => CoberturaParser.ParseMethods(ReportInput.FromFile(bad)));
        foreach (var path in new[]
        {
            good,
            bad
        }

        )
        {
            await using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.IsTrue(exclusive.CanRead);
        }
    }

    // ── Errors ────────────────────────────────────────────────────────────────
    [TestMethod]
    public void Parse_MalformedInput_CarriesSourceNameAndCoordinates()
    {
        var input = ReportInput.FromBytes("ci-job-7", "<coverage>\n  <packages>\n    <class "u8.ToArray());
        var ex = Assert.ThrowsExactly<ReportParseException>(() => CoberturaParser.Parse(input));
        Assert.AreEqual("ci-job-7", ex.SourceName);
        Assert.AreEqual(ex.InnerException.LineNumber, ex.LineNumber);
        Assert.AreEqual(ex.InnerException.LinePosition, ex.LinePosition);
        Assert.AreEqual(3, ex.LineNumber);
        Assert.IsGreaterThan(0, ex.LinePosition);
        Assert.AreEqual(ex.InnerException.Message, ex.Message);
        Assert.DoesNotContain("ci-job-7", ex.Message);
    }

    [TestMethod]
    public void ParseMethods_MalformedSecondInput_NamesTheSecond()
    {
        var good = ReportInput.FromBytes("first", Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1)).ToBytes());
        var bad = ReportInput.FromBytes("second", "<coverage><packages>"u8.ToArray());
        var ex = Assert.ThrowsExactly<ReportParseException>(() => CoberturaParser.ParseMethods([good, bad]));
        Assert.AreEqual("second", ex.SourceName);
    }

    [TestMethod]
    public void Parse_CallerStream_MalformedXml_ThrowsPlainXmlException()
    {
        // A caller-owned stream has no source name to attach; the reader's exception passes through.
        using var stream = new MemoryStream("this is not xml at all"u8.ToArray());
        // The assertion executes and completes within the captured resource lifetime.
        // ReSharper disable once AccessToDisposedClosure
        Assert.ThrowsExactly<XmlException>(() => CoberturaParser.Parse(stream));
        Assert.ThrowsExactly<XmlException>(() => CoberturaParser.ParseMethods(new MemoryStream("<x>"u8.ToArray())));
    }

    // ── Character cap ─────────────────────────────────────────────────────────
    [TestMethod]
    public void MaxChars_AppliesPerDocument_NotToTheInputSetAsAWhole()
    {
        var bytes = Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1)).ToBytes();
        var cap = bytes.Length + 16; // ASCII payload: one document fits, two do not
        var inputs = new[]
        {
            ReportInput.FromBytes("one", bytes),
            ReportInput.FromBytes("two", bytes)
        };
        Assert.ContainsSingle(CoberturaParser.Parse(inputs, maxChars: cap).Files);
        Assert.IsEmpty(CoberturaParser.ParseMethods(inputs, maxChars: cap).Warnings);
        var ex = Assert.ThrowsExactly<ReportParseException>(() => CoberturaParser.Parse(inputs, maxChars: 50));
        Assert.AreEqual("one", ex.SourceName);
        Assert.ThrowsExactly<ReportParseException>(() => CoberturaParser.ParseMethods(inputs, maxChars: 50));
    }

    [TestMethod]
    public void MaxChars_AppliesToFileInputsThroughTheResolver()
    {
        var bytes = Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1)).ToBytes();
        var file = _ws.Write("cap/coverage.cobertura.xml", bytes);
        Assert.ThrowsExactly<ReportParseException>(() => CoberturaParser.Parse(ReportResolver.Resolve(file), maxChars: 50));
        Assert.ThrowsExactly<ReportParseException>(() => CoberturaParser.Parse(ReportResolver.Resolve(_ws.PathOf("cap")), maxChars: 50));
        Assert.ContainsSingle(CoberturaParser.Parse(ReportResolver.Resolve(file), maxChars: 1_000_000).Files);
        Assert.ContainsSingle(CoberturaParser.Parse(ReportResolver.Resolve(_ws.PathOf("cap")), maxChars: 1_000_000).Files);
    }

    [TestMethod]
    public void MaxChars_Zero_MeansNoCap()
    {
        var bytes = Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1)).ToBytes();
        CoberturaParser.Parse(ReportInput.FromBytes("doc", bytes), maxChars: 0);
        CoberturaParser.ParseMethods(ReportInput.FromBytes("doc", bytes), maxChars: 0);
    }

    // ── Sync/async parity beyond the happy path ───────────────────────────────
    [TestMethod]
    public async Task ParseAsync_MalformedDocument_ThrowsLikeSync()
    {
        var bytes = "<coverage><packages>"u8.ToArray();
        using var sync = new MemoryStream(bytes);
        using var async = new MemoryStream(bytes);
        // The assertion executes and completes within the captured resource lifetime.
        // ReSharper disable once AccessToDisposedClosure
        var syncEx = Assert.ThrowsExactly<XmlException>(() => CoberturaParser.Parse(sync));
        var asyncEx = (await Assert.ThrowsExactlyAsync<XmlException>(async () => await CoberturaParser.ParseAsync(async)));
        Assert.AreEqual(syncEx.LineNumber, asyncEx.LineNumber);
        Assert.AreEqual(syncEx.LinePosition, asyncEx.LinePosition);
    }
}
