using System.Reflection;

namespace DotCov.CoverageHook;

/// <summary>Publishes pending Microsoft static coverage hits before a process terminates.</summary>
public static class CoverageFlusher
{
    /// <summary>
    /// Flushes only the active collector's injected trackers in already loaded assemblies.
    /// Joins the tracker's background publication before its final flush. Does not handle
    /// the exception, change exits, or infer missing hits.
    /// </summary>
    public static void Flush(TextWriter diagnostics)
    {
        var session = Environment.GetEnvironmentVariable("CODE_COVERAGE_SESSION_NAME");
        if (string.IsNullOrEmpty(session)) return;

        var name = "Microsoft.CodeCoverage.Instrumentation.Static.Tracker.StaticManagedTrackerTemplate_" + session;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = assembly.GetType(name, throwOnError: false);
                var flush = type?.GetMethod("Flush", BindingFlags.Public | BindingFlags.Static,
                    binder: null, types: Type.EmptyTypes, modifiers: null);
                if (flush is not null && flush.ReturnType == typeof(void))
                {
                    // Flush clears private hits before writing them. Join its periodic worker
                    // first so termination cannot interrupt a publication already in flight.
                    // The Microsoft loop ends when its sleep is interrupted.
                    if (type!.GetField("_flushThread", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null)
                        is Thread { IsAlive: true } thread && thread != Thread.CurrentThread)
                    {
                        thread.Interrupt();
                        if (!thread.Join(1000))
                            throw new TimeoutException("coverage publication thread did not stop within one second");
                    }
                    flush.Invoke(null, null);
                }
            }
            catch (Exception exception)
            {
                // A hook must leave the original unhandled exception and its exit untouched.
                diagnostics.WriteLine($"dotcov: coverage flush failed for {assembly.GetName().Name}: {exception.GetBaseException().Message}");
            }
        }
    }
}
