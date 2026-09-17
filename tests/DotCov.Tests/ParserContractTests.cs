using System.Text;
using System.Xml;
using DotCov.Formatters;
using DotCov.Tests.Infrastructure;
using TUnit.Assertions.Enums;

namespace DotCov.Tests;

/// <summary>
/// One document, four ways in — caller-owned stream (sync and async), in-memory input, file
/// input — must yield the same report. Also the responsibilities that sit at the input
/// boundary: multi-input merging, who disposes which stream, structured error coordinates,
/// and the per-document character cap.
/// </summary>
public sealed class ParserContractTests : IDisposable
{
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-contract-");

    public void Dispose() => _ws.Dispose();

    public static IEnumerable<(string Name, Cobertura Document)> Documents()
    {
        yield return ("two plain classes", Cobertura.NewDoc()
            .AddClass("src/A.cs", c => c.Line(1, hits: 3).Line(2, hits: 0))
            .AddClass("src/B.cs", c => c.Line(5, hits: 1)));

        yield return ("coverlet layout with methods, branches and conditions", Cobertura.NewDoc()
            .WithSource("/repo")
            .AddClass("src/A.cs", "MyApp.A", c => c
                .Method("M", "()", "2", m => m.Line(1, hits: 1).Line(2, hits: 0))
                .Line(1, hits: 1)
                .Line(2, hits: 0)
                .BranchWithConditions(3, "50% (1/2)", (0, "50%"))));

        yield return ("same file under two class blocks", Cobertura.NewDoc()
            .AddClass("src/A.cs", "MyApp.A", c => c.Line(1, hits: 0).Line(2, hits: 1))
            .AddClass("src/A.cs", "MyApp.A/<M>d__1", c => c.Line(1, hits: 2).Line(3, hits: 0)));

        yield return ("malformed hits and condition strings warn", Cobertura.NewDoc()
            .AddClass("src/A.cs", "MyApp.A", c => c
                .Method("M", "()", "1", m => m.MalformedLine("7", "lots"))
                .MalformedLine("7", "lots")
                .Branch(8, "garbage")));

        yield return ("two source roots and a windows path", Cobertura.NewDoc()
            .WithSource("c:\\work\\repo")
            .WithSource("/other")
            .AddClass("src\\Win.cs", c => c.Line(1, hits: 1)));
    }

    private static string Canonical(CoverageReport report) =>
        JsonFormatter.Format(report) + "\n" + string.Join("|", report.SourceRoots);

    private static IEnumerable<string> Canonical(MethodCoverageReport report) =>
        report.Methods.Select(m =>
            $"{m.File}|{m.ClassName}|{m.MethodName}|{m.Signature}|{m.StartLine}-{m.EndLine}|{m.LinesHit}/{m.LinesTotal}|{m.Complexity}|" +
            string.Join(",", m.LineHits.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}")))
        .Concat(report.Warnings.Select(w => $"warning:{w.Kind}:{w.File}:{w.Line}:{w.Detail}"))
        .Concat(report.SourceRoots.Select(r => $"root:{r}"));

    // ── Same document, every source ───────────────────────────────────────────

    [Test]
    [MethodDataSource(nameof(Documents))]
    public async Task Parse_MemoryAndFileAndStreamSources_YieldTheSameReport(string name, Cobertura document)
    {
        var bytes = document.ToBytes();
        var path = _ws.Write($"{name.GetHashCode():x}/coverage.cobertura.xml", bytes);

        using var syncStream = new MemoryStream(bytes);
        using var asyncStream = new MemoryStream(bytes);
        var viaStream = CoberturaParser.Parse(syncStream);
        var viaAsync = await CoberturaParser.ParseAsync(asyncStream);
        var viaBytes = CoberturaParser.Parse(ReportInput.FromBytes(name, bytes));
        var viaFile = CoberturaParser.Parse(ReportInput.FromFile(path));
        var viaResolver = CoberturaParser.Parse(ReportResolver.Resolve(path));

        var expected = Canonical(viaStream);
        await Assert.That(Canonical(viaAsync)).IsEqualTo(expected).Because($"{name}: async");
        await Assert.That(Canonical(viaBytes)).IsEqualTo(expected).Because($"{name}: bytes");
        await Assert.That(Canonical(viaFile)).IsEqualTo(expected).Because($"{name}: file");
        await Assert.That(Canonical(viaResolver)).IsEqualTo(expected).Because($"{name}: resolver");
        await Assert.That(viaStream.Warnings.Count).IsEqualTo(viaFile.Warnings.Count);
    }

    [Test]
    [MethodDataSource(nameof(Documents))]
    public async Task ParseMethods_MemoryAndFileAndStreamSources_YieldTheSameReport(string name, Cobertura document)
    {
        var bytes = document.ToBytes();
        var path = _ws.Write($"m-{name.GetHashCode():x}/coverage.cobertura.xml", bytes);

        using var stream = new MemoryStream(bytes);
        var viaStream = CoberturaParser.ParseMethods(stream);
        var viaBytes = CoberturaParser.ParseMethods(ReportInput.FromBytes(name, bytes));
        var viaFile = CoberturaParser.ParseMethods(ReportInput.FromFile(path));
        var viaResolver = CoberturaParser.ParseMethods(ReportResolver.Resolve(path));

        var expected = Canonical(viaStream).ToList();
        await Assert.That(Canonical(viaBytes)).IsEquivalentTo(expected, CollectionOrdering.Matching).Because($"{name}: bytes");
        await Assert.That(Canonical(viaFile)).IsEquivalentTo(expected, CollectionOrdering.Matching).Because($"{name}: file");
        await Assert.That(Canonical(viaResolver)).IsEquivalentTo(expected, CollectionOrdering.Matching).Because($"{name}: resolver");
    }

    [Test]
    public async Task Parse_WarningsAreIdenticalOnFileAndMethodPaths()
    {
        // The shared line decoder raises MalformedHits once per malformed <line>; both
        // aggregations observe the same document, so both carry the warning.
        var bytes = Cobertura.NewDoc()
            .AddClass("src/A.cs", "MyApp.A", c => c.Method("M", "()", "1", m => m.MalformedLine("7", "lots")))
            .ToBytes();

        var files = CoberturaParser.Parse(ReportInput.FromBytes("doc", bytes));
        var methods = CoberturaParser.ParseMethods(ReportInput.FromBytes("doc", bytes));

        var w = await Assert.That(files.Warnings).HasSingleItem();
        await Assert.That(w.Kind).IsEqualTo(CoverageWarningKind.MalformedHits);
        await Assert.That(methods.Warnings).IsEquivalentTo(files.Warnings, CollectionOrdering.Matching);
    }

    // ── Multiple inputs ───────────────────────────────────────────────────────

    [Test]
    public async Task Parse_MultipleInputs_MergesInInputOrder()
    {
        var first = ReportInput.FromBytes("r1", Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1)).ToBytes());
        var second = ReportInput.FromBytes("r2", Cobertura.NewDoc().AddClass("a.cs", c => c.Line(2, 1).Line(3, 0)).ToBytes());
        var third = ReportInput.FromBytes("r3", Cobertura.NewDoc().AddClass("b.cs", c => c.Line(1, 1)).ToBytes());

        var report = CoberturaParser.Parse([first, second, third]);

        await Assert.That(report.Files.Count).IsEqualTo(2);
        var a = report.Files.Single(f => f.Path == "a.cs");
        await Assert.That(a.LinesTotal).IsEqualTo(3);
        await Assert.That(a.LinesHit).IsEqualTo(2);
        await Assert.That(report.Files[0].Path).IsEqualTo("a.cs");   // first-seen order survives the merge
    }

    [Test]
    public async Task Parse_NoInputs_IsAnEmptyReport_NotAnError()
    {
        var report = CoberturaParser.Parse(Array.Empty<ReportInput>());
        var methods = CoberturaParser.ParseMethods(Array.Empty<ReportInput>());

        await Assert.That(report.Files).IsEmpty();
        await Assert.That(report.Evaluate(80).Outcome).IsEqualTo(GateOutcome.NoData);
        await Assert.That(methods.Methods).IsEmpty();
        await Assert.That(methods.Warnings).IsEmpty();
    }

    // ── Stream ownership ──────────────────────────────────────────────────────

    [Test]
    public async Task Parse_CallerStream_IsLeftOpen()
    {
        var bytes = Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1)).ToBytes();
        using var sync = new MemoryStream(bytes);
        using var async = new MemoryStream(bytes);
        using var methods = new MemoryStream(bytes);

        CoberturaParser.Parse(sync);
        await CoberturaParser.ParseAsync(async);
        CoberturaParser.ParseMethods(methods);

        await Assert.That(sync.CanRead).IsTrue();
        await Assert.That(async.CanRead).IsTrue();
        await Assert.That(methods.CanRead).IsTrue();
    }

    [Test]
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

        foreach (var path in new[] { good, bad })
        {
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            await Assert.That(exclusive.CanRead).IsTrue();
        }
    }

    // ── Errors ────────────────────────────────────────────────────────────────

    [Test]
    public async Task Parse_MalformedInput_CarriesSourceNameAndCoordinates()
    {
        var input = ReportInput.FromBytes("ci-job-7", "<coverage>\n  <packages>\n    <class "u8.ToArray());

        var ex = Assert.ThrowsExactly<ReportParseException>(() => CoberturaParser.Parse(input));

        await Assert.That(ex.SourceName).IsEqualTo("ci-job-7");
        await Assert.That(ex.LineNumber).IsEqualTo(ex.InnerException.LineNumber);
        await Assert.That(ex.LinePosition).IsEqualTo(ex.InnerException.LinePosition);
        await Assert.That(ex.LineNumber).IsEqualTo(3);
        await Assert.That(ex.LinePosition).IsGreaterThan(0);
        await Assert.That(ex.Message).IsEqualTo(ex.InnerException.Message);
        await Assert.That(ex.Message).DoesNotContain("ci-job-7");
    }

    [Test]
    public async Task ParseMethods_MalformedSecondInput_NamesTheSecond()
    {
        var good = ReportInput.FromBytes("first", Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1)).ToBytes());
        var bad = ReportInput.FromBytes("second", "<coverage><packages>"u8.ToArray());

        var ex = Assert.ThrowsExactly<ReportParseException>(() => CoberturaParser.ParseMethods([good, bad]));

        await Assert.That(ex.SourceName).IsEqualTo("second");
    }

    [Test]
    public void Parse_CallerStream_MalformedXml_ThrowsPlainXmlException()
    {
        // A caller-owned stream has no source name to attach; the reader's exception passes through.
        using var stream = new MemoryStream("this is not xml at all"u8.ToArray());

        Assert.ThrowsExactly<XmlException>(() => CoberturaParser.Parse(stream));
        Assert.ThrowsExactly<XmlException>(() => CoberturaParser.ParseMethods(new MemoryStream("<x>"u8.ToArray())));
    }

    // ── Character cap ─────────────────────────────────────────────────────────

    [Test]
    public async Task MaxChars_AppliesPerDocument_NotToTheInputSetAsAWhole()
    {
        var bytes = Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1)).ToBytes();
        var cap = bytes.Length + 16;   // ASCII payload: one document fits, two do not
        var inputs = new[] { ReportInput.FromBytes("one", bytes), ReportInput.FromBytes("two", bytes) };

        await Assert.That(CoberturaParser.Parse(inputs, maxChars: cap).Files).HasSingleItem();
        await Assert.That(CoberturaParser.ParseMethods(inputs, maxChars: cap).Warnings).IsEmpty();

        var ex = Assert.ThrowsExactly<ReportParseException>(() => CoberturaParser.Parse(inputs, maxChars: 50));
        await Assert.That(ex.SourceName).IsEqualTo("one");
        Assert.ThrowsExactly<ReportParseException>(() => CoberturaParser.ParseMethods(inputs, maxChars: 50));
    }

    [Test]
    public async Task MaxChars_AppliesToFileInputsThroughTheResolver()
    {
        var bytes = Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1)).ToBytes();
        var file = _ws.Write("cap/coverage.cobertura.xml", bytes);

        Assert.ThrowsExactly<ReportParseException>(() => CoberturaParser.Parse(ReportResolver.Resolve(file), maxChars: 50));
        Assert.ThrowsExactly<ReportParseException>(() => CoberturaParser.Parse(ReportResolver.Resolve(_ws.PathOf("cap")), maxChars: 50));
        await Assert.That(CoberturaParser.Parse(ReportResolver.Resolve(file), maxChars: 1_000_000).Files).HasSingleItem();
        await Assert.That(CoberturaParser.Parse(ReportResolver.Resolve(_ws.PathOf("cap")), maxChars: 1_000_000).Files).HasSingleItem();
    }

    [Test]
    public void MaxChars_Zero_MeansNoCap()
    {
        var bytes = Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1)).ToBytes();

        CoberturaParser.Parse(ReportInput.FromBytes("doc", bytes), maxChars: 0);
        CoberturaParser.ParseMethods(ReportInput.FromBytes("doc", bytes), maxChars: 0);
    }

    // ── Sync/async parity beyond the happy path ───────────────────────────────

    [Test]
    public async Task ParseAsync_MalformedDocument_ThrowsLikeSync()
    {
        var bytes = Encoding.UTF8.GetBytes("<coverage><packages>");
        using var sync = new MemoryStream(bytes);
        using var async = new MemoryStream(bytes);

        var syncEx = Assert.ThrowsExactly<XmlException>(() => CoberturaParser.Parse(sync));
        var asyncEx = (await Assert.ThrowsExactlyAsync<XmlException>(async () => await CoberturaParser.ParseAsync(async)))!;

        await Assert.That(asyncEx.LineNumber).IsEqualTo(syncEx.LineNumber);
        await Assert.That(asyncEx.LinePosition).IsEqualTo(syncEx.LinePosition);
    }
}
