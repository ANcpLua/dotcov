namespace DotCov.Fallout;

/// <summary>
/// Appends markdown to the file named by <c>GITHUB_STEP_SUMMARY</c>. The summary is
/// decoration: an unset variable is a no-op and an unwritable target reports false so the
/// caller can warn without changing the coverage verdict.
/// </summary>
internal static class GitHubStepSummary
{
    public const string Variable = "GITHUB_STEP_SUMMARY";

    public static string? ConfiguredPath => Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } path ? path : null;

    public static bool TryAppend(string? path, string markdown)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            File.AppendAllText(path, markdown);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}
