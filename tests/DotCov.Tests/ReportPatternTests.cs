namespace DotCov.Tests;
/// <summary>
/// The pattern contract: only <c>filename</c> and <c>**/filename</c>, split on the literal
/// <c>**/</c> prefix and never on the host's directory separator. Any other directory component
/// is rejected: discarding it would let <c>coverage/*.xml</c> match the wrong scope and reach
/// the gate as NoData, the most invisible misconfiguration.
/// </summary>
[TestClass]
public sealed class ReportPatternTests
{
    [TestMethod]
    [DataRow("coverage.cobertura.xml", "coverage.cobertura.xml", false)]
    [DataRow("**/coverage.cobertura.xml", "coverage.cobertura.xml", true)]
    [DataRow("coverage.xml", "coverage.xml", false)]
    [DataRow("*.xml", "*.xml", false)]
    [DataRow("**/*.xml", "*.xml", true)]
    [DataRow("**coverage.xml", "**coverage.xml", false)] // '**' inside the NAME: filename-shaped, top level only
    [DataRow("**/**coverage.xml", "**coverage.xml", true)]
    public void Parse_ValidPattern_SplitsNameAndRecursion(string pattern, string fileName, bool recursive)
    {
        var parsed = ReportPattern.Parse(pattern);
        Assert.AreEqual(fileName, parsed.FileName);
        Assert.AreEqual(recursive, parsed.Recursive);
        Assert.AreEqual(pattern, parsed.ToString());
        Assert.IsTrue(ReportPattern.TryParse(pattern, out var viaTry));
        Assert.AreEqual(parsed, viaTry);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("**/")]
    [DataRow("coverage/*.xml")]
    [DataRow("unit/**/coverage.cobertura.xml")]
    [DataRow("**/sub/coverage.xml")]
    [DataRow(@"src\coverage.cobertura.xml")]
    [DataRow(@"**\coverage.cobertura.xml")]
    [DataRow("/coverage.xml")]
    [DataRow("Fixtures/sample.cobertura.xml")]
    public void Parse_InvalidPattern_ThrowsNamingTheParameter(string pattern)
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(() => ReportPattern.Parse(pattern));
        Assert.AreEqual("pattern", ex.ParamName);
        Assert.Contains($"Unsupported pattern '{pattern}'", ex.Message);
        Assert.IsFalse(ReportPattern.TryParse(pattern, out var result));
        Assert.IsNull(result);
    }

    [TestMethod]
    public void Default_IsARecursiveCoberturaPattern()
    {
        Assert.AreEqual("*cobertura*.xml", ReportPattern.Default.FileName);
        Assert.IsTrue(ReportPattern.Default.Recursive);
        Assert.AreEqual(ReportPattern.DefaultText, ReportPattern.Default.ToString());
        Assert.AreEqual(ReportPattern.Default, ReportPattern.Parse(ReportPattern.DefaultText));
    }

    [TestMethod]
    public void Equality_IsByNameAndRecursion_Ordinal()
    {
        Assert.AreEqual(ReportPattern.Parse("coverage.xml"), ReportPattern.Parse("coverage.xml"));
        Assert.AreNotEqual(ReportPattern.Parse("**/coverage.xml"), ReportPattern.Parse("coverage.xml"));
        Assert.AreNotEqual(ReportPattern.Parse("Coverage.xml"), ReportPattern.Parse("coverage.xml"));
        Assert.AreEqual(ReportPattern.Parse("coverage.xml").GetHashCode(), ReportPattern.Parse("coverage.xml").GetHashCode());
    }

    [TestMethod]
    public void Parse_Null_ThrowsArgumentNull() => Assert.ThrowsExactly<ArgumentNullException>(() => ReportPattern.Parse(null!));
}
