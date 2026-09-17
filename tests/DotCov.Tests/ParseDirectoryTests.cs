using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

public sealed class ParseDirectoryTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dotcov-parse-dir-").FullName;

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private string Write(string relative, Cobertura builder)
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, builder.ToBytes());
        return full;
    }

    [Test]
    public async Task ParseDirectory_NoFilesFound_ReturnsEmptyReport()
    {
        var report = CoberturaParser.Parse(ReportResolver.ResolveDirectory(_root, ReportPattern.Default));

        await Assert.That(report.Files).IsEmpty();
        await Assert.That(report).IsSameReferenceAs(CoverageReport.Empty);
    }

    [Test]
    public async Task ParseDirectory_SingleFile_ParsesIt()
    {
        Write("coverage.cobertura.xml",
            Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1)));

        var report = CoberturaParser.Parse(ReportResolver.ResolveDirectory(_root, ReportPattern.Default));

        await Assert.That(report.Files).HasSingleItem();
    }

    [Test]
    public async Task ParseDirectory_MultipleNestedFiles_MergesAcrossPaths()
    {
        Write("test1/coverage.cobertura.xml",
            Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1).Line(2, 0)));
        Write("test2/coverage.cobertura.xml",
            Cobertura.NewDoc().AddClass("b.cs", c => c.Line(1, 1)));

        var report = CoberturaParser.Parse(ReportResolver.ResolveDirectory(_root, ReportPattern.Default));

        await Assert.That(report.Files.Count).IsEqualTo(2);
    }

    [Test]
    public async Task ParseDirectory_SameFileAcrossReports_AggregatesCounts()
    {
        Write("r1/coverage.cobertura.xml",
            Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1)));
        Write("r2/coverage.cobertura.xml",
            Cobertura.NewDoc().AddClass("a.cs", c => c.Line(2, 1).Line(3, 0)));

        var report = CoberturaParser.Parse(ReportResolver.ResolveDirectory(_root, ReportPattern.Default));

        var file = await Assert.That(report.Files).HasSingleItem();
        await Assert.That(file.LinesTotal).IsEqualTo(3);
        await Assert.That(file.LinesHit).IsEqualTo(2);
    }

    [Test]
    public async Task ParseDirectory_NonRecursivePattern_OnlyScansTopLevel()
    {
        Write("top.xml", Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1)));
        Write("nested/inner.xml", Cobertura.NewDoc().AddClass("b.cs", c => c.Line(1, 1)));

        var report = CoberturaParser.Parse(ReportResolver.ResolveDirectory(_root, ReportPattern.Parse("*.xml")));

        await Assert.That(report.Files).HasSingleItem();
    }

    [Test]
    public async Task ParseDirectory_StarStarInsideNamePortion_DoesNotRecurse()
    {
        // '**coverage.xml' has an empty directory prefix, so the pattern gate classifies it
        // as filename-shaped — top level only. Recursion is decided by the same admitted
        // '**/' prefix, never re-derived from a '**' that happens to sit inside the name
        // (which silently pulled in subdirectory files against the documented contract).
        // The name portion still wildcard-matches top-level files via Directory.GetFiles.
        Write("coverage.xml", Cobertura.NewDoc().AddClass("top.cs", c => c.Line(1, 1)));
        Write("nested/coverage.xml", Cobertura.NewDoc().AddClass("deep.cs", c => c.Line(1, 1)));

        var report = CoberturaParser.Parse(ReportResolver.ResolveDirectory(_root, ReportPattern.Parse("**coverage.xml")));

        await Assert.That(report.Files.Single().Path).IsEqualTo("top.cs");
    }

    [Test]
    public async Task ParsePath_File_DelegatesToParseFile()
    {
        var path = Write("c.xml", Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1)));

        var report = CoberturaParser.Parse(ReportResolver.Resolve(path));

        await Assert.That(report.Files).HasSingleItem();
    }

    [Test]
    public async Task ParsePath_Directory_DelegatesToParseDirectory()
    {
        Write("coverage.cobertura.xml", Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1)));

        var report = CoberturaParser.Parse(ReportResolver.Resolve(_root));

        await Assert.That(report.Files).HasSingleItem();
    }
}