using DotCov.Tests.Infrastructure;
using TUnit.Assertions.Enums;

namespace DotCov.Tests;

/// <summary>
/// The one place a path becomes a set of reports: a file is itself, a directory is searched
/// with a <see cref="ReportPattern"/> — hidden directories included — in ordinal path order.
/// A missing path is an error; an existing directory with no match is an empty set, and what
/// an empty set means is the gate's decision, not the resolver's.
/// </summary>
public sealed class ReportResolverTests : IDisposable
{
    private readonly TempWorkspace _ws = TempWorkspace.Create("dotcov-resolver-");

    public void Dispose() => _ws.Dispose();

    private static Cobertura Doc(string file) => Cobertura.NewDoc().AddClass(file, c => c.Line(1, 1));

    private static IEnumerable<string> Names(IEnumerable<ReportInput> inputs) => inputs.Select(i => i.SourceName);

    // ── Files ─────────────────────────────────────────────────────────────────

    [Test]
    public async Task Resolve_File_YieldsThatFileRegardlessOfPattern()
    {
        var path = _ws.Write("anything.xml", Doc("a.cs"));

        var inputs = ReportResolver.Resolve(path, ReportPattern.Parse("coverage.cobertura.xml"));

        var input = await Assert.That(inputs).HasSingleItem();
        await Assert.That(input.SourceName).IsEqualTo(path);
    }

    [Test]
    public async Task Resolve_MissingPath_ThrowsFileNotFound()
    {
        var missing = _ws.PathOf("does-not-exist");

        var ex = Assert.ThrowsExactly<FileNotFoundException>(() => ReportResolver.Resolve(missing));

        await Assert.That(ex.Message).Contains(missing);
        await Assert.That(ex.FileName).IsEqualTo(missing);
    }

    [Test]
    public void ResolveDirectory_MissingDirectory_ThrowsDirectoryNotFound() =>
        Assert.ThrowsExactly<DirectoryNotFoundException>(() =>
            ReportResolver.ResolveDirectory(_ws.PathOf("nope"), ReportPattern.Default));

    // ── Directories ───────────────────────────────────────────────────────────

    [Test]
    [MatrixDataSource]
    public async Task Resolve_DefaultPattern_FindsReportNamesAcrossDirectoryLayouts(
        [Matrix("coverage.cobertura.xml", "coverage.cobertura.170926234734218.xml",
            "dotcov.coverage.cobertura.170926234734218.xml", "test-run.cobertura.xml")] string name,
        [Matrix("", "run", ".hidden")] string relativeDirectory)
    {
        var report = _ws.Write(Path.Combine(relativeDirectory, name), Doc("a.cs"));
        _ws.Write(Path.Combine(relativeDirectory, "coverage.json"), "{}");
        _ws.Write(Path.Combine(relativeDirectory, "test-results.xml"), "<results />");

        if (OperatingSystem.IsWindows() && relativeDirectory.StartsWith('.'))
        {
            var directory = _ws.PathOf(relativeDirectory);
            File.SetAttributes(directory, File.GetAttributes(directory) | FileAttributes.Hidden);
        }

        var input = await Assert.That(ReportResolver.Resolve(_ws.Root)).HasSingleItem();
        await Assert.That(input.SourceName).IsEqualTo(report);
    }

    [Test]
    public async Task Resolve_DirectoryWithoutMatches_YieldsEmptySet()
    {
        _ws.Write("notes.txt", "not a report");
        _ws.Write("nested/other.xml", Doc("a.cs"));

        await Assert.That(ReportResolver.Resolve(_ws.Root)).IsEmpty();
        await Assert.That(ReportResolver.ResolveDirectory(_ws.Root, ReportPattern.Default)).IsEmpty();
    }

    [Test]
    public async Task Resolve_RecursivePattern_FindsReportsInHiddenDirectories()
    {
        // CI drops artifacts under dot-folders; a report that exists but is not found is a
        // silent NoData. The default enumeration skips Hidden|System — the resolver must not.
        var top = _ws.Write("coverage.cobertura.xml", Doc("top.cs"));
        var hidden = _ws.Write(".hidden/coverage.cobertura.xml", Doc("hidden.cs"));
        var deep = _ws.Write(".artifacts/.cache/run-1/coverage.cobertura.xml", Doc("deep.cs"));
        if (OperatingSystem.IsWindows())
            foreach (var dir in new[] { ".hidden", ".artifacts", ".artifacts/.cache" })
                File.SetAttributes(_ws.PathOf(dir), File.GetAttributes(_ws.PathOf(dir)) | FileAttributes.Hidden);

        var inputs = ReportResolver.Resolve(_ws.Root);

        await Assert.That(Names(inputs)).IsEquivalentTo(
            new[] { top, hidden, deep }.Order(StringComparer.Ordinal), CollectionOrdering.Matching);
    }

    [Test]
    public async Task Resolve_Order_IsOrdinalByFullPath_NotCreationOrder()
    {
        var z = _ws.Write("z/coverage.cobertura.xml", Doc("z.cs"));
        var b = _ws.Write("B/coverage.cobertura.xml", Doc("b.cs"));
        var a = _ws.Write("a/coverage.cobertura.xml", Doc("a.cs"));

        var inputs = ReportResolver.Resolve(_ws.Root);

        // Ordinal: uppercase 'B' sorts before lowercase 'a'.
        await Assert.That(Names(inputs)).IsEquivalentTo([b, a, z], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ResolveDirectory_NonRecursivePattern_ScansTopLevelOnly()
    {
        var top = _ws.Write("top.xml", Doc("a.cs"));
        _ws.Write("nested/inner.xml", Doc("b.cs"));

        var inputs = ReportResolver.ResolveDirectory(_ws.Root, ReportPattern.Parse("*.xml"));

        await Assert.That(Names(inputs)).IsEquivalentTo([top], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ResolveDirectory_StarStarInsideNamePortion_DoesNotRecurse()
    {
        // '**coverage.xml' is filename-shaped (no '**/' prefix): top level only, and the name
        // portion still wildcard-matches.
        var top = _ws.Write("coverage.xml", Doc("top.cs"));
        _ws.Write("nested/coverage.xml", Doc("deep.cs"));

        var inputs = ReportResolver.ResolveDirectory(_ws.Root, ReportPattern.Parse("**coverage.xml"));

        await Assert.That(Names(inputs)).IsEquivalentTo([top], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ResolveDirectory_CustomPattern_DiscoversNonCoverletReportNames()
    {
        var gcovr = _ws.Write("gcovr/coverage.xml", Doc("a.c"));

        await Assert.That(ReportResolver.Resolve(_ws.Root)).IsEmpty();
        await Assert.That(Names(ReportResolver.Resolve(_ws.Root, ReportPattern.Parse("**/coverage.xml"))))
            .IsEquivalentTo([gcovr], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Resolve_DirectoryInputs_OpenIndependentStreams()
    {
        var path = _ws.Write("coverage.cobertura.xml", Doc("a.cs"));
        var input = ReportResolver.Resolve(_ws.Root).Single();

        using var first = input.OpenStream();
        using var second = input.OpenStream();

        await Assert.That(input.SourceName).IsEqualTo(path);
        await Assert.That(first.Position).IsEqualTo(0L);
        await Assert.That(second.Position).IsEqualTo(0L);
        await Assert.That(first.Length).IsEqualTo(second.Length);
    }

    // ── ReportInput ───────────────────────────────────────────────────────────

    [Test]
    public async Task ReportInput_FromBytes_EachOpenStartsAtTheBeginning()
    {
        var bytes = Doc("a.cs").ToBytes();
        var input = ReportInput.FromBytes("in-memory", bytes);

        using (var first = input.OpenStream())
            first.ReadByte();
        using var second = input.OpenStream();

        await Assert.That(input.SourceName).IsEqualTo("in-memory");
        await Assert.That(second.Position).IsEqualTo(0L);
        await Assert.That(second.CanWrite).IsFalse();
        await Assert.That(second.Length).IsEqualTo(bytes.Length);
    }

    [Test]
    public void ReportInput_RejectsMissingNameOrPayload()
    {
        Assert.ThrowsExactly<ArgumentException>(() => ReportInput.FromFile(""));
        Assert.ThrowsExactly<ArgumentException>(() => ReportInput.FromBytes("", []));
        Assert.ThrowsExactly<ArgumentNullException>(() => ReportInput.FromBytes("x", null!));
    }
}
