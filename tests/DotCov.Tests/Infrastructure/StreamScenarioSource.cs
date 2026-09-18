namespace DotCov.Tests.Infrastructure;

public sealed record StreamScenario(int ChunkSize)
{
    public ScriptedReadStream Open(byte[] bytes, bool asyncOnly = false) =>
        new(bytes, ChunkSize, asyncOnly);

    public override string ToString() => $"read chunks of {ChunkSize} byte(s)";
}

public sealed class StreamScenarioSourceAttribute : DataSourceGeneratorAttribute<StreamScenario>
{
    protected override IEnumerable<Func<StreamScenario>> GenerateDataSources(DataGeneratorMetadata metadata)
    {
        yield return () => new StreamScenario(1);
        yield return () => new StreamScenario(7);
        yield return () => new StreamScenario(64);
    }
}
