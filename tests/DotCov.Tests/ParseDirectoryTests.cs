using DotCov.Tests.Infrastructure;
using Xunit;

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

    [Fact]
    public void ParseDirectory_NoFilesFound_ReturnsEmptyReport()
    {
        var report = CoberturaParser.ParseDirectory(_root);

        Assert.Empty(report.Files);
        Assert.Same(CoverageReport.Empty, report);
    }

    [Fact]
    public void ParseDirectory_SingleFile_ParsesIt()
    {
        Write("coverage.cobertura.xml",
            Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1)));

        var report = CoberturaParser.ParseDirectory(_root);

        Assert.Single(report.Files);
    }

    [Fact]
    public void ParseDirectory_MultipleNestedFiles_MergesAcrossPaths()
    {
        Write("test1/coverage.cobertura.xml",
            Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1).Line(2, 0)));
        Write("test2/coverage.cobertura.xml",
            Cobertura.NewDoc().AddClass("b.cs", c => c.Line(1, 1)));

        var report = CoberturaParser.ParseDirectory(_root);

        Assert.Equal(2, report.Files.Count);
    }

    [Fact]
    public void ParseDirectory_SameFileAcrossReports_AggregatesCounts()
    {
        Write("r1/coverage.cobertura.xml",
            Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1)));
        Write("r2/coverage.cobertura.xml",
            Cobertura.NewDoc().AddClass("a.cs", c => c.Line(2, 1).Line(3, 0)));

        var report = CoberturaParser.ParseDirectory(_root);

        var file = Assert.Single(report.Files);
        Assert.Equal(3, file.LinesTotal);
        Assert.Equal(2, file.LinesHit);
    }

    [Fact]
    public void ParseDirectory_NonRecursivePattern_OnlyScansTopLevel()
    {
        Write("top.xml", Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1)));
        Write("nested/inner.xml", Cobertura.NewDoc().AddClass("b.cs", c => c.Line(1, 1)));

        var report = CoberturaParser.ParseDirectory(_root, "*.xml");

        Assert.Single(report.Files);
    }

    [Fact]
    public void ParsePath_File_DelegatesToParseFile()
    {
        var path = Write("c.xml", Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1)));

        var report = CoberturaParser.ParsePath(path);

        Assert.Single(report.Files);
    }

    [Fact]
    public void ParsePath_Directory_DelegatesToParseDirectory()
    {
        Write("coverage.cobertura.xml", Cobertura.NewDoc().AddClass("a.cs", c => c.Line(1, 1)));

        var report = CoberturaParser.ParsePath(_root);

        Assert.Single(report.Files);
    }
}
