namespace DotCov.Tests;

/// <summary>
/// The public equality and try-parse surface of <see cref="ReportPattern"/>: a null pattern
/// is a failed parse, and the object overload of <c>Equals</c> agrees with the typed one.
/// </summary>
public sealed class ReportPatternEdgeTests
{
    [Test]
    public async Task TryParse_Null_FailsWithoutResult()
    {
        var parsed = ReportPattern.TryParse(null, out var result);

        await Assert.That(parsed).IsFalse();
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task EqualsObject_SameShape_IsEqualWithEqualHashCode()
    {
        object other = ReportPattern.Parse("**/*cobertura*.xml");

        await Assert.That(ReportPattern.Default.Equals(other)).IsTrue();
        await Assert.That(ReportPattern.Default.GetHashCode()).IsEqualTo(other.GetHashCode());
    }

    [Test]
    public async Task EqualsObject_DifferentRecursionOrName_IsNotEqual()
    {
        await Assert.That(ReportPattern.Default.Equals((object)ReportPattern.Parse("*cobertura*.xml"))).IsFalse();
        await Assert.That(ReportPattern.Default.Equals((object)ReportPattern.Parse("**/*COBERTURA*.xml"))).IsFalse();
    }

    [Test]
    public async Task EqualsObject_NullOrOtherType_IsNotEqual()
    {
        object text = ReportPattern.DefaultText;
        await Assert.That(ReportPattern.Default.Equals(text)).IsFalse();
        await Assert.That(ReportPattern.Default.Equals((object?)null)).IsFalse();
    }
}
