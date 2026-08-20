namespace DotCov.Formatters;

/// <summary>
/// Tiny ANSI wrapper for terminal coloring. Pass <c>enabled: false</c> and every call is a no-op,
/// so the formatter code stays identical for piped/redirected output.
/// </summary>
public readonly struct AnsiPen(bool enabled)
{
    private const string Esc = "\e[";

    public bool Enabled => enabled;

    public string Bold(string s) => enabled ? $"{Esc}1m{s}{Esc}22m" : s;
    public string Dim(string s) => enabled ? $"{Esc}2m{s}{Esc}22m" : s;
    public string Green(string s) => enabled ? $"{Esc}32m{s}{Esc}39m" : s;
    public string Yellow(string s) => enabled ? $"{Esc}33m{s}{Esc}39m" : s;
    public string Red(string s) => enabled ? $"{Esc}31m{s}{Esc}39m" : s;
    public string Cyan(string s) => enabled ? $"{Esc}36m{s}{Esc}39m" : s;

    /// <summary>
    /// Color a coverage percentage cell by its rate (0..1). A null rate means unmeasured and
    /// falls to the dim arm - no colour verdict is asserted over data that does not exist.
    /// </summary>
    public string Rate(string text, double? rate) => rate switch
    {
        >= 0.75 => Green(text),
        >= 0.5 => Yellow(text),
        > 0 => Red(text),
        _ => Dim(text)
    };

    /// <summary>
    /// Color a delta cell — green for improvement, red for regression, dim for unchanged.
    /// Shares <see cref="CoverageDiff.MovementEpsilon"/> with the diff classification so a
    /// delta never colors as movement while the diff calls it unchanged (or vice versa).
    /// A delta of exactly ±epsilon counts as movement, matching
    /// <see cref="CoverageDiff.Compare"/>'s strict "closer to zero than epsilon" noise test.
    /// </summary>
    public string Delta(string text, double? delta) => delta switch
    {
        >= CoverageDiff.MovementEpsilon => Green(text),
        <= -CoverageDiff.MovementEpsilon => Red(text),
        _ => Dim(text)
    };
}
