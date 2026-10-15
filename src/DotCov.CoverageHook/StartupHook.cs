using DotCov.CoverageHook;

// The runtime requires this exact type name in the global namespace.
// ReSharper disable once CheckNamespace
public static class StartupHook
{
    static int _initialized;

    public static void Initialize()
    {
        if (Interlocked.Exchange(ref _initialized, 1) is 0)
        {
            AppDomain.CurrentDomain.UnhandledException += static (_, _) => CoverageFlusher.Flush(Console.Error);
            AppDomain.CurrentDomain.ProcessExit += static (_, _) => CoverageFlusher.Flush(Console.Error);
        }
    }
}
