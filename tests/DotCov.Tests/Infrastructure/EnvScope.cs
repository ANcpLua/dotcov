namespace DotCov.Tests.Infrastructure;

/// <summary>
/// IDisposable scope that sets a process env var on enter and restores the previous
/// value on dispose. Lets <see cref="DotCov.Formatters.Ansi"/> tests exercise the
/// real env-var precedence cascade without mocking the runtime.
///
/// The env table is process-global, so every test class that sets OR reads one of these
/// variables carries <c>[NotInParallel(ProcessState.Environment)]</c>; the shared key
/// serializes writers and readers against each other while the rest of the suite stays parallel.
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

/// <summary>Keys for <c>[NotInParallel]</c> constraints over process-wide state.</summary>
public static class ProcessState
{
    /// <summary>Process environment variables (<c>GITHUB_STEP_SUMMARY</c>, the ANSI color cascade).</summary>
    public const string Environment = "process-environment";
}
