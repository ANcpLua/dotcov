namespace DotCov.Tests.Infrastructure;

public enum MergeGrouping { LeftFold, RightFold, Balanced, LeftNested, RightNested }

public sealed record MergeScenario(
    string Order, MergeGrouping Grouping, int ExpectedBranchesHit, GateOutcome ExpectedGate, bool HasIdentityMismatch)
{
    public CoverageReport Merge()
    {
        var reports = Order.Select(Parse).ToArray();
        var a = reports[0];
        var b = reports[1];
        var c = reports[2];
        var d = reports[3];
        return Grouping switch
        {
            MergeGrouping.LeftFold => CoverageReport.Merge(CoverageReport.Merge(CoverageReport.Merge(a, b), c), d),
            MergeGrouping.RightFold => CoverageReport.Merge(a, CoverageReport.Merge(b, CoverageReport.Merge(c, d))),
            MergeGrouping.Balanced => CoverageReport.Merge(CoverageReport.Merge(a, b), CoverageReport.Merge(c, d)),
            MergeGrouping.LeftNested => CoverageReport.Merge(CoverageReport.Merge(a, CoverageReport.Merge(b, c)), d),
            MergeGrouping.RightNested => CoverageReport.Merge(a, CoverageReport.Merge(CoverageReport.Merge(b, c), d)),
            _ => throw new ArgumentOutOfRangeException(nameof(Grouping))
        };
    }

    private static CoverageReport Parse(char name)
    {
        var document = name switch
        {
            'A' => Cobertura.NewDoc().AddClass("src/Widget.cs", c => c
                .Line(1, 3).Line(2, 0)
                .BranchWithConditions(5, "25% (1/4)", (1, "0%"), (2, "50%"))),
            'B' => Cobertura.NewDoc().AddClass("src/Widget.cs", c => c
                .Line(1, 0).Line(2, 1)
                .BranchWithConditions(5, "25% (1/4)", (1, "50%"), (2, "0%"))),
            'N' => Cobertura.NewDoc().AddClass("src/Widget.cs", c => c
                .Line(3, 0).Branch(5, "25% (1/4)")),
            'C' => Cobertura.NewDoc().AddClass("src/Widget.cs", c => c
                .BranchWithConditions(5, "25% (1/4)", (1, "0%"), (3, "50%"))),
            _ => throw new ArgumentOutOfRangeException(nameof(name))
        };
        return CoberturaParser.Parse(ReportInput.FromBytes(name.ToString(), document.ToBytes()));
    }

    public override string ToString() => $"{Order}, {Grouping}";
}

public sealed class MergeScenarioSourceAttribute : DataSourceGeneratorAttribute<MergeScenario>
{
    protected override IEnumerable<Func<MergeScenario>> GenerateDataSources(DataGeneratorMetadata metadata)
    {
        // A+B prove 2/4 branches. N has no condition detail. A repeated adds no coverage;
        // C changes the condition identities, requiring the conservative raw 1/4 result.
        foreach (var (inputs, hits, outcome, mismatch) in new[]
        {
            ("ABNA", 2, GateOutcome.Pass, false),
            ("ABNC", 1, GateOutcome.Fail, true)
        })
        foreach (var order in Permutations(inputs))
        foreach (var grouping in Enum.GetValues<MergeGrouping>())
            yield return () => new MergeScenario(order, grouping, hits, outcome, mismatch);
    }

    private static IEnumerable<string> Permutations(string inputs)
    {
        if (inputs.Length is 0)
        {
            yield return "";
            yield break;
        }

        foreach (var first in inputs.Distinct())
        foreach (var suffix in Permutations(inputs.Remove(inputs.IndexOf(first), 1)))
            yield return first + suffix;
    }
}
