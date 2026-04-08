using System.Text;
using System.Xml;
using Xunit;

namespace DotCov.Tests;

public sealed class CoberturaParserTests
{
    private const string FixturePath = "Fixtures/sample.cobertura.xml";

    [Fact]
    public void Parse_SampleFixture_ReturnsThreeFiles()
    {
        var report = CoberturaParser.ParseFile(FixturePath);
        Assert.Equal(3, report.Files.Count);
    }

    [Fact]
    public void Parse_FullyCoveredClass_ReportsAllLinesHit()
    {
        var report = CoberturaParser.ParseFile(FixturePath);
        var calculator = report.Files.Single(f => f.Path == "src/Calculator.cs");

        Assert.Equal(4, calculator.LinesTotal);
        Assert.Equal(4, calculator.LinesHit);
        Assert.Equal(1.0, calculator.LineRate);
    }

    [Fact]
    public void Parse_PartiallyCoveredClass_ReportsCorrectHitCount()
    {
        var report = CoberturaParser.ParseFile(FixturePath);
        var parser = report.Files.Single(f => f.Path == "src/Parser.cs");

        Assert.Equal(5, parser.LinesTotal);
        Assert.Equal(3, parser.LinesHit);
        Assert.Equal(0.6, parser.LineRate);
    }

    [Fact]
    public void Parse_UncoveredClass_ReportsZeroLineRate()
    {
        var report = CoberturaParser.ParseFile(FixturePath);
        var unused = report.Files.Single(f => f.Path == "src/Unused.cs");

        Assert.Equal(3, unused.LinesTotal);
        Assert.Equal(0, unused.LinesHit);
        Assert.Equal(0.0, unused.LineRate);
    }

    [Fact]
    public void Parse_FullBranchCoverage_ReportsAllBranchesHit()
    {
        var report = CoberturaParser.ParseFile(FixturePath);
        var calculator = report.Files.Single(f => f.Path == "src/Calculator.cs");

        Assert.Equal(2, calculator.BranchesTotal);
        Assert.Equal(2, calculator.BranchesHit);
        Assert.Equal(1.0, calculator.BranchRate);
    }

    [Fact]
    public void Parse_PartialBranches_ExtractsConditionCoverageCorrectly()
    {
        var report = CoberturaParser.ParseFile(FixturePath);
        var parser = report.Files.Single(f => f.Path == "src/Parser.cs");

        Assert.Equal(4, parser.BranchesTotal);
        Assert.Equal(1, parser.BranchesHit);
        Assert.Equal(0.25, parser.BranchRate);
    }

    [Fact]
    public void Parse_NoBranches_ReportsFullBranchRate()
    {
        var report = CoberturaParser.ParseFile(FixturePath);
        var unused = report.Files.Single(f => f.Path == "src/Unused.cs");

        Assert.Equal(0, unused.BranchesTotal);
        Assert.Equal(1.0, unused.BranchRate);
    }

    [Fact]
    public void Report_AggregateTotals_SumsAcrossAllFiles()
    {
        var report = CoberturaParser.ParseFile(FixturePath);

        Assert.Equal(12, report.TotalLines);
        Assert.Equal(7, report.TotalLinesHit);
        Assert.Equal(6, report.TotalBranches);
        Assert.Equal(3, report.TotalBranchesHit);
    }

    [Fact]
    public void MeetsThreshold_AboveMinimum_ReturnsTrue()
    {
        var report = CoberturaParser.ParseFile(FixturePath);
        Assert.True(report.MeetsThreshold(50));
    }

    [Fact]
    public void MeetsThreshold_BelowMinimum_ReturnsFalse()
    {
        var report = CoberturaParser.ParseFile(FixturePath);
        Assert.False(report.MeetsThreshold(80));
    }

    [Fact]
    public void MeetsThreshold_WithBranchMinimum_ChecksBoth()
    {
        var report = CoberturaParser.ParseFile(FixturePath);
        Assert.True(report.MeetsThreshold(50, 50));
        Assert.False(report.MeetsThreshold(50, 60));
    }

    [Fact]
    public void BelowPercent_ReturnsOnlyFilesUnderThreshold()
    {
        var report = CoberturaParser.ParseFile(FixturePath);
        var below80 = report.BelowPercent(80).ToList();

        Assert.Equal(2, below80.Count);
        Assert.Contains(below80, f => f.Path == "src/Parser.cs");
        Assert.Contains(below80, f => f.Path == "src/Unused.cs");
        Assert.DoesNotContain(below80, f => f.Path == "src/Calculator.cs");
    }

    [Fact]
    public void Parse_XmlWithDtd_Throws()
    {
        var malicious = """
            <?xml version="1.0"?>
            <!DOCTYPE coverage [<!ENTITY xxe SYSTEM "file:///etc/passwd">]>
            <coverage><packages><package><classes>
              <class name="X" filename="x.cs"><lines>
                <line number="1" hits="1" branch="false"/>
              </lines></class>
            </classes></package></packages></coverage>
            """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(malicious));
        Assert.Throws<XmlException>(() => CoberturaParser.Parse(stream));
    }

    [Fact]
    public void Parse_EmptyPackages_ReturnsEmptyReport()
    {
        var xml = """
            <?xml version="1.0"?>
            <coverage><packages></packages></coverage>
            """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var report = CoberturaParser.Parse(stream);

        Assert.Empty(report.Files);
        Assert.Equal(1.0, report.LineRate);
    }

    [Fact]
    public void Parse_ClassWithNoLines_ReportsZeroTotals()
    {
        var xml = """
            <?xml version="1.0"?>
            <coverage><packages><package><classes>
              <class name="Empty" filename="empty.cs" line-rate="0" branch-rate="0">
              </class>
            </classes></package></packages></coverage>
            """;

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        var report = CoberturaParser.Parse(stream);

        Assert.Single(report.Files);
        Assert.Equal(0, report.Files[0].LinesTotal);
        Assert.Equal(1.0, report.Files[0].LineRate);
    }

    // ── Merge ──

    [Fact]
    public void Merge_TwoReports_CombinesFilesByPath()
    {
        var a = new CoverageReport([new FileCoverage("a.cs", 3, 10, 0, 0)]);
        var b = new CoverageReport([new FileCoverage("b.cs", 5, 10, 0, 0)]);

        var merged = CoverageReport.Merge(a, b);

        Assert.Equal(2, merged.Files.Count);
    }

    [Fact]
    public void Merge_SameFile_AggregatesCounts()
    {
        var a = new CoverageReport([new FileCoverage("a.cs", 3, 10, 1, 2)]);
        var b = new CoverageReport([new FileCoverage("a.cs", 5, 8, 2, 4)]);

        var merged = CoverageReport.Merge(a, b);

        Assert.Single(merged.Files);
        Assert.Equal(8, merged.Files[0].LinesHit);
        Assert.Equal(18, merged.Files[0].LinesTotal);
        Assert.Equal(3, merged.Files[0].BranchesHit);
        Assert.Equal(6, merged.Files[0].BranchesTotal);
    }

    // ── ParsePath ──

    [Fact]
    public void ParsePath_WithFile_ParsesSuccessfully()
    {
        var report = CoberturaParser.ParsePath(FixturePath);
        Assert.Equal(3, report.Files.Count);
    }

    [Fact]
    public void ParsePath_WithNonexistentPath_Throws()
    {
        Assert.Throws<FileNotFoundException>(() => CoberturaParser.ParsePath("nonexistent"));
    }
}
