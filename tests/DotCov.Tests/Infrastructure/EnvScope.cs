namespace DotCov.Tests.Infrastructure;

/// <summary>
/// IDisposable scope that sets a process env var on enter and restores the previous
/// value on dispose. Lets <see cref="DotCov.Formatters.Ansi"/> tests exercise the
/// real env-var precedence cascade without mocking the runtime.
///
/// Tests using this MUST live in <c>EnvCollection</c> so xUnit serialises them —
/// the process-global env table is shared across the test runner.
/// </summary>
public sealed class EnvScope : IDisposable
{
    private readonly Dictionary<string, string?> _previous = [];

    public EnvScope(params (string Name, string? Value)[] vars)
    {
        foreach (var (name, value) in vars)
        {
            _previous[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
    }

    public static EnvScope Clear(params string[] names) =>
        new(names.Select(n => (n, (string?)null)).ToArray());

    public void Dispose()
    {
        foreach (var (name, value) in _previous)
            Environment.SetEnvironmentVariable(name, value);
    }
}

[Xunit.CollectionDefinition(nameof(EnvCollection), DisableParallelization = true)]
public sealed class EnvCollection;
