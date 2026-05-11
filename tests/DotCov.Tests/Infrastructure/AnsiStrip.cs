using System.Text.RegularExpressions;

namespace DotCov.Tests.Infrastructure;

/// <summary>
/// Strips ANSI CSI/SGR escapes so formatter golden tests can assert on plain text
/// regardless of whether the formatter ran with color enabled.
/// </summary>
public static partial class AnsiStrip
{
    [GeneratedRegex(@"\x1b\[[0-9;]*[A-Za-z]")]
    private static partial Regex AnsiPattern();

    public static string From(string input) => AnsiPattern().Replace(input, string.Empty);

    public static bool ContainsAnsi(string input) => AnsiPattern().IsMatch(input);
}
