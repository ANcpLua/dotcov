namespace DotCov.Tests;
/// <summary>
/// The public equality and try-parse surface of <see cref = "ReportPattern"/>: a null pattern
/// is a failed parse, and the object overload of <c>Equals</c> agrees with the typed one.
/// </summary>
[TestClass]
public sealed class ReportPatternEdgeTests
{
    [TestMethod]
    public void TryParse_Null_FailsWithoutResult()
    {
        var parsed = ReportPattern.TryParse(null, out var result);
        Assert.IsFalse(parsed);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void EqualsObject_SameShape_IsEqualWithEqualHashCode()
    {
        object other = ReportPattern.Parse("**/*cobertura*.xml");
        Assert.IsTrue(ReportPattern.Default.Equals(other));
        Assert.AreEqual(other.GetHashCode(), ReportPattern.Default.GetHashCode());
    }

    [TestMethod]
    public void EqualsObject_DifferentRecursionOrName_IsNotEqual()
    {
        Assert.IsFalse(ReportPattern.Default.Equals((object)ReportPattern.Parse("*cobertura*.xml")));
        Assert.IsFalse(ReportPattern.Default.Equals((object)ReportPattern.Parse("**/*COBERTURA*.xml")));
    }

    [TestMethod]
    public void EqualsObject_NullOrOtherType_IsNotEqual()
    {
        object text = ReportPattern.DefaultText;
        Assert.IsFalse(ReportPattern.Default.Equals(text));
        Assert.IsFalse(ReportPattern.Default.Equals((object? )null));
    }
}
