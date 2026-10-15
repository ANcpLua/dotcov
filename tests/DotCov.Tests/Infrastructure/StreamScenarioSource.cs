using System.Reflection;

namespace DotCov.Tests.Infrastructure;
public sealed record StreamScenario(int ChunkSize)
{
    public ScriptedReadStream Open(byte[] bytes, bool asyncOnly = false) => new(bytes, ChunkSize, asyncOnly);
    public override string ToString() => $"read chunks of {ChunkSize} byte(s)";
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class StreamScenarioSourceAttribute : Attribute, ITestDataSource
{
    public string GetDisplayName(MethodInfo methodInfo, object? []? data) => $"{methodInfo.Name}({data![0]})";
    public IEnumerable<object? []> GetData(MethodInfo methodInfo)
    {
        yield return[new StreamScenario(1)];
        yield return[new StreamScenario(7)];
        yield return[new StreamScenario(64)];
    }
}
