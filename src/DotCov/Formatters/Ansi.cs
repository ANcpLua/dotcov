using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace DotCov.Formatters;

/// <summary>
/// ANSI terminal detection + Windows VT bootstrap.
/// Detection follows the de-facto cascade used by chalk/supports-color/clicolors:
/// NO_COLOR → off, FORCE_COLOR/CLICOLOR_FORCE → on, TERM=dumb → off,
/// known CI systems → on (their log viewers render escape codes),
/// otherwise → on iff stdout is a TTY.
/// </summary>
public static partial class Ansi
{
    public static bool IsSupported() => IsSupported(Console.IsOutputRedirected);

    /// <summary>
    /// Public overload that takes an explicit redirect flag — lets callers (and tests) drive
    /// the cascade without depending on the global console state.
    /// </summary>
    public static bool IsSupported(bool isOutputRedirected)
    {
        if (HasEnv("NO_COLOR")) return false;                           // no-color.org
        if (IsTrue("FORCE_COLOR")) return true;                         // chalk convention
        if (IsTrue("CLICOLOR_FORCE")) return true;                      // BSD convention
        if (Env("TERM") is "dumb") return false;
        if (Env("CLICOLOR") is "0") return false;
        if (IsKnownCiWithAnsi()) return true;                           // GH/GitLab/Circle/etc.
        return !isOutputRedirected;
    }

    /// <summary>
    /// Enables virtual-terminal processing on the current Windows console so that ANSI
    /// escape codes are interpreted instead of printed literally. No-op on non-Windows
    /// and on consoles that don't expose a usable handle (e.g. tests, redirected streams).
    /// </summary>
    /// <remarks>
    /// Coverage-excluded: this is a thin OS-conditional dispatcher whose two branches can
    /// only be exercised on different platforms (non-Windows hits the early return, Windows
    /// hits the try/catch). The unit tests verify the contract "never throws on any platform";
    /// branch coverage on a single OS would otherwise penalise an unreachable path.
    /// </remarks>
    [ExcludeFromCodeCoverage(Justification =
        "OS-conditional dispatcher: the non-Windows early return and the Windows try/catch are " +
        "mutually exclusive per platform, so single-OS branch coverage cannot exercise both.")]
    public static void EnableOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;
        try { EnableWindowsVt(); } catch { /* console may not be attached — never fatal */ }
    }

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name);

    private static bool HasEnv(string name) => !string.IsNullOrEmpty(Env(name));

    private static bool IsTrue(string name) =>
        Env(name) is { } v && v.Length > 0 && v is not "0" && !string.Equals(v, "false", StringComparison.OrdinalIgnoreCase);

    private static bool IsKnownCiWithAnsi()
    {
        // CI systems whose log viewers render ANSI. Sourced from each platform's docs as of 2026-05.
        // Keep allowlist-only — generic CI=true is too noisy (some build agents capture raw text).
        return HasEnv("GITHUB_ACTIONS")
            || HasEnv("GITLAB_CI")
            || HasEnv("CIRCLECI")
            || HasEnv("BUILDKITE")
            || HasEnv("TF_BUILD")              // Azure DevOps / Azure Pipelines
            || HasEnv("DRONE")
            || HasEnv("APPVEYOR")
            || HasEnv("TRAVIS")
            || HasEnv("WOODPECKER")
            || HasEnv("FORGEJO_ACTIONS")
            || HasEnv("GITEA_ACTIONS");
    }

    #region Windows VT bootstrap
    // Excluded from coverage: reachable only on Windows runners. The Linux/macOS CI matrix
    // can't exercise these paths, and they're thin Win32 wrappers — coverage tooling on
    // non-Windows would otherwise penalise an unreachable platform branch.

    [SupportedOSPlatform("windows")]
    [ExcludeFromCodeCoverage(Justification =
        "Windows-only Win32 console interop; the guard branches are unreachable on the Linux/macOS CI matrix.")]
    private static void EnableWindowsVt()
    {
        const int StdOutputHandle = -11;
        const uint EnableVirtualTerminalProcessing = 0x0004;

        var handle = GetStdHandle(StdOutputHandle);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return;
        if (!GetConsoleMode(handle, out var mode)) return;
        SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    [ExcludeFromCodeCoverage]
    private static partial IntPtr GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    [ExcludeFromCodeCoverage]
    private static partial bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    [ExcludeFromCodeCoverage]
    private static partial bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

    #endregion
}
