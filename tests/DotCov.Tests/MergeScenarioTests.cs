using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;
[TestClass]
public sealed class MergeScenarioTests
{
    [TestMethod]
    [MergeScenarioSource]
    public void Merge_ReportOrderGroupingAndDuplicates_PreserveMeasurementsAndGate(MergeScenario scenario)
    {
        var report = scenario.Merge();
        var file = Assert.ContainsSingle(report.Files);
        Assert.AreEqual("src/Widget.cs", file.Path);
        Assert.AreEqual(4, file.LinesTotal);
        Assert.AreEqual(3, file.LinesHit);
        Assert.AreEqual(3, file.LineHits[1]);
        Assert.AreEqual(4, file.BranchesTotal);
        Assert.AreEqual(scenario.ExpectedBranchesHit, file.BranchesHit);
        Assert.AreEqual(scenario.ExpectedGate, report.Evaluate(70, 40).Outcome);
        Assert.AreEqual(scenario.HasIdentityMismatch, report.Warnings.Any(w => w.Kind == CoverageWarningKind.ConditionIdentityMismatch));
    }
}
