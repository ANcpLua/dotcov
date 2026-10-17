using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

[TestClass]
public sealed class MergeScenarioTests
{
    public static IEnumerable<TestDataRow<MergeScenario>> MergeCases()
    {
        // A+B prove 2/4 branches. N has no condition detail. A repeated adds no coverage;
        // C changes the condition identities, requiring the conservative raw 1/4 result.
        (string Inputs, int Hits, GateOutcome Outcome, bool Mismatch)[] cases =
        [
            ("ABNA", 2, GateOutcome.Pass, false),
            ("ABNC", 1, GateOutcome.Fail, true)
        ];

        foreach (var (inputs, hits, outcome, mismatch) in cases)
        foreach (var order in Permutations(inputs))
        foreach (var grouping in Enum.GetValues<MergeGrouping>())
        {
            var scenario = new MergeScenario(order, grouping, hits, outcome, mismatch);
            yield return new TestDataRow<MergeScenario>(scenario)
            {
                ArgumentsDisplayName = $"{order}, {grouping}"
            };
        }
    }

    [TestMethod]
    [DynamicData(nameof(MergeCases))]
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

    private static IEnumerable<string> Permutations(string inputs)
    {
        if (inputs.Length == 0)
        {
            yield return "";
            yield break;
        }

        foreach (var first in inputs.Distinct())
        foreach (var suffix in Permutations(inputs.Remove(inputs.IndexOf(first), 1)))
            yield return first + suffix;
    }
}
