namespace DotCov;

/// <summary>
/// dotcov's process exit codes: one table for the CLI, the Fallout component, and the
/// <c>gate.exitCode</c> every JSON gate object carries, so a caller can tell a failed gate from
/// one that could not measure or was never armed without parsing any text.
/// </summary>
public static class ExitCodes
{
    /// <summary>A measured pass.</summary>
    public const int Pass = 0;

    /// <summary>FAIL: measured and below a threshold.</summary>
    public const int Fail = 1;

    /// <summary>Invalid usage: unknown command or flag, invalid value, missing or extra argument.</summary>
    public const int Usage = 2;

    /// <summary>NODATA: the gate lacks the measurements it needs.</summary>
    public const int NoData = 3;

    /// <summary>DISABLED: no active threshold.</summary>
    public const int Disabled = 4;

    /// <summary>Report, IO, upload, or badge failure.</summary>
    public const int Error = 5;

    /// <summary>The <c>dotnet test</c> run of <c>dotcov test</c> failed; coverage was not evaluated.</summary>
    public const int TestRunFailed = 6;

    /// <summary>The exit code a gate's outcome maps to.</summary>
    public static int For(GateOutcome outcome) => outcome switch
    {
        GateOutcome.Pass => Pass,
        GateOutcome.Fail => Fail,
        GateOutcome.NoData => NoData,
        _ => Disabled
    };
}
