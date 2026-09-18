using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

public sealed class MergeScenarioTests
{
    [Test]
    [MergeScenarioSource]
    public async Task Merge_ReportOrderGroupingAndDuplicates_PreserveMeasurementsAndGate(MergeScenario scenario)
    {
        var report = scenario.Merge();

        var file = await Assert.That(report.Files).HasSingleItem();
        await Assert.That(file.Path).IsEqualTo("src/Widget.cs");
        await Assert.That(file.LinesTotal).IsEqualTo(4);
        await Assert.That(file.LinesHit).IsEqualTo(3);
        await Assert.That(file.LineHits[1]).IsEqualTo(3);
        await Assert.That(file.BranchesTotal).IsEqualTo(4);
        await Assert.That(file.BranchesHit).IsEqualTo(scenario.ExpectedBranchesHit);
        await Assert.That(report.Evaluate(70, 40).Outcome).IsEqualTo(scenario.ExpectedGate);
        await Assert.That(report.Warnings.Any(w => w.Kind == CoverageWarningKind.ConditionIdentityMismatch))
            .IsEqualTo(scenario.HasIdentityMismatch);
    }
}
