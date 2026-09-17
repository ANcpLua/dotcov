namespace DotCov.Tests;

/// <summary>
/// The pattern contract: only <c>filename</c> and <c>**/filename</c>, split on the literal
/// <c>**/</c> prefix and never on the host's directory separator. Any directory component
/// used to be silently discarded, so <c>coverage/*.xml</c> matched the wrong scope and flowed
/// into the gate as NoData — the most invisible misconfiguration.
/// </summary>
public sealed class ReportPatternTests
{
    [Test]
    [Arguments("coverage.cobertura.xml", "coverage.cobertura.xml", false)]
    [Arguments("**/coverage.cobertura.xml", "coverage.cobertura.xml", true)]
    [Arguments("coverage.xml", "coverage.xml", false)]
    [Arguments("*.xml", "*.xml", false)]
    [Arguments("**/*.xml", "*.xml", true)]
    [Arguments("**coverage.xml", "**coverage.xml", false)]   // '**' inside the NAME: filename-shaped, top level only
    [Arguments("**/**coverage.xml", "**coverage.xml", true)]
    public async Task Parse_ValidPattern_SplitsNameAndRecursion(string pattern, string fileName, bool recursive)
    {
        var parsed = ReportPattern.Parse(pattern);

        await Assert.That(parsed.FileName).IsEqualTo(fileName);
        await Assert.That(parsed.Recursive).IsEqualTo(recursive);
        await Assert.That(parsed.ToString()).IsEqualTo(pattern);
        await Assert.That(ReportPattern.TryParse(pattern, out var viaTry)).IsTrue();
        await Assert.That(viaTry).IsEqualTo(parsed);
    }

    [Test]
    [Arguments("")]
    [Arguments("**/")]
    [Arguments("coverage/*.xml")]
    [Arguments("unit/**/coverage.cobertura.xml")]
    [Arguments("**/sub/coverage.xml")]
    [Arguments(@"src\coverage.cobertura.xml")]
    [Arguments(@"**\coverage.cobertura.xml")]
    [Arguments("/coverage.xml")]
    [Arguments("Fixtures/sample.cobertura.xml")]
    public async Task Parse_InvalidPattern_ThrowsNamingTheParameter(string pattern)
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(() => ReportPattern.Parse(pattern));

        await Assert.That(ex.ParamName).IsEqualTo("pattern");
        await Assert.That(ex.Message).Contains($"Unsupported pattern '{pattern}'");
        await Assert.That(ReportPattern.TryParse(pattern, out var result)).IsFalse();
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Default_IsTheRecursiveCoverletName()
    {
        await Assert.That(ReportPattern.Default.FileName).IsEqualTo("coverage.cobertura.xml");
        await Assert.That(ReportPattern.Default.Recursive).IsTrue();
        await Assert.That(ReportPattern.Default.ToString()).IsEqualTo(ReportPattern.DefaultText);
        await Assert.That(ReportPattern.Parse(ReportPattern.DefaultText)).IsEqualTo(ReportPattern.Default);
    }

    [Test]
    public async Task Equality_IsByNameAndRecursion_Ordinal()
    {
        await Assert.That(ReportPattern.Parse("coverage.xml")).IsEqualTo(ReportPattern.Parse("coverage.xml"));
        await Assert.That(ReportPattern.Parse("coverage.xml")).IsNotEqualTo(ReportPattern.Parse("**/coverage.xml"));
        await Assert.That(ReportPattern.Parse("coverage.xml")).IsNotEqualTo(ReportPattern.Parse("Coverage.xml"));
        await Assert.That(ReportPattern.Parse("coverage.xml").GetHashCode()).IsEqualTo(ReportPattern.Parse("coverage.xml").GetHashCode());
    }

    [Test]
    public void Parse_Null_ThrowsArgumentNull() =>
        Assert.ThrowsExactly<ArgumentNullException>(() => ReportPattern.Parse(null!));
}
