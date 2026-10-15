using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;
[TestClass]
public sealed class ScenarioSourceTests
{
    [TestMethod]
    public void StreamCases_AreRepeatableAndHaveDistinctNames()
    {
        var method = typeof(ParserStreamScenarioTests).GetMethod(nameof(ParserStreamScenarioTests.ParseMethods_FragmentedInput_KeepsMethodMeasurementsSeparate))!;
        var source = new StreamScenarioSourceAttribute();
        var first = source.GetData(method).ToArray();
        var second = source.GetData(method).ToArray();
        Assert.AreSequenceEqual([1, 7, 64], first.Select(row => Assert.IsInstanceOfType<StreamScenario>(Assert.ContainsSingle(row)).ChunkSize));
        Assert.AreSequenceEqual(first.Select(row => (StreamScenario)row[0]!), second.Select(row => (StreamScenario)row[0]!));
        Assert.HasCount(3, first.Select(row => source.GetDisplayName(method, row)).Distinct());
    }

    [TestMethod]
    public void MergeCases_CoverEveryPermutationAndGroupingWithoutDuplicateRows()
    {
        var method = typeof(MergeScenarioTests).GetMethods().Single(method => method.GetCustomAttributes(typeof(TestMethodAttribute), false).Length != 0);
        var source = new MergeScenarioSourceAttribute();
        var first = source.GetData(method).ToArray();
        var second = source.GetData(method).ToArray();
        var scenarios = first.Select(row => Assert.IsInstanceOfType<MergeScenario>(Assert.ContainsSingle(row))).ToArray();
        Assert.HasCount(180, scenarios);
        Assert.HasCount(180, first.Select(row => source.GetDisplayName(method, row)).Distinct());
        Assert.AreSequenceEqual(scenarios, second.Select(row => (MergeScenario)row[0]!));
        foreach (var group in scenarios.GroupBy(scenario => scenario.Order))
            Assert.AreSequenceEqual(Enum.GetValues<MergeGrouping>(), group.Select(scenario => scenario.Grouping), SequenceOrder.InAnyOrder);
        Assert.HasCount(60, scenarios.Where(scenario => !scenario.HasIdentityMismatch));
        Assert.HasCount(120, scenarios.Where(scenario => scenario.HasIdentityMismatch));
    }
}
