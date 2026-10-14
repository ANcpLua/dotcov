using System.Text;
using static System.FormattableString;

namespace DotCov.Formatters;

/// <summary>
/// Line numbers spelled as comma-separated runs, e.g. <c>21,23-24</c>: the form every format uses
/// for uncovered lines, a fraction of the tokens a number array costs.
/// </summary>
internal static class LineRanges
{
    internal static string Format(IEnumerable<int> lines)
    {
        var sb = new StringBuilder();
        int? start = null;
        var end = 0;
        foreach (var line in lines.Order())
        {
            // A repeated number extends nothing; the next one up extends the run.
            if (start is not null && line <= end + 1)
            {
                end = Math.Max(end, line);
                continue;
            }

            Append(sb, start, end);
            start = end = line;
        }

        Append(sb, start, end);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, int? start, int end)
    {
        if (start is not { } s) return;
        if (sb.Length > 0) sb.Append(',');
        sb.Append(s == end ? Invariant($"{s}") : Invariant($"{s}-{end}"));
    }

    /// <summary>Partially covered branch lines as <c>20 (1/2), 26 (0/4)</c>.</summary>
    internal static string Branches(IReadOnlyList<BranchDetail> branches) =>
        string.Join(", ", branches.Select(static b => Invariant($"{b.Line} ({b.Covered}/{b.Total})")));
}
