using System.Buffers;
using System.Text;
using System.Text.Json;
using static System.FormattableString;

namespace DotCov.Formatters;

/// <summary>
/// Renders a <see cref="CrapReport"/> + <see cref="CrapGateResult"/> as a worst-first method
/// table (terminal), JSON (machines), or markdown (PR summaries). All numeric rendering is
/// invariant-formatted — same locale-proof contract as the other formatters — and offender
/// highlighting routes through the same threshold comparison as the verdict
/// (<see cref="CrapAnalysis.Exceeds"/>), so display and exit code can never drift.
/// </summary>
public static class CrapFormatter
{
    public static string Format(CrapReport report, CrapGateResult gate, int? top = null) =>
        Format(report, gate, top, color: false);

    public static string Format(CrapReport report, CrapGateResult gate, int? top, bool color)
    {
        var pen = new AnsiPen(color);
        var sb = new StringBuilder();
        var rows = WorstFirst(report, top);
        var maxName = Math.Max("Method".Length, rows.Count > 0 ? rows.Max(static m => m.Method.Length) : 0);

        var headerPlain = $"{"Method".PadRight(maxName)}  {"Comp",5}  {"Cov %",8}  {"CRAP",8}";
        sb.AppendLine(pen.Bold(pen.Cyan(headerPlain)));
        sb.AppendLine(pen.Dim(new string('-', headerPlain.Length)));

        foreach (var m in rows)
        {
            var over = CrapAnalysis.Exceeds(m.Score, gate.MaxCrap);
            var crapCell = Invariant($"{m.Score,8:F1}");
            sb.AppendLine(
                $"{m.Method.PadRight(maxName)}  " +
                Invariant($"{m.Complexity,5}  ") +
                Invariant($"{m.Coverage * 100,7:F1}%  ") +
                (over ? pen.Red(crapCell) : pen.Green(crapCell)));
        }

        sb.AppendLine(pen.Dim(new string('-', headerPlain.Length)));
        if (top is { } t && report.Methods.Count > t)
            sb.AppendLine(pen.Dim(Invariant($"... {report.Methods.Count - t} more methods below (--top {t})")));

        AppendHonestyTrailers(sb, report, pen);
        return sb.ToString();
    }

    public static string FormatMarkdown(CrapReport report, CrapGateResult gate, int? top = null)
    {
        var sb = new StringBuilder();
        var badge = gate.Outcome switch
        {
            GateOutcome.Pass => " ✅",
            GateOutcome.Fail => " ❌",
            _ => " ⚠️",
        };

        sb.AppendLine($"## CRAP Report{badge}");
        sb.AppendLine();
        sb.AppendLine(Invariant($"**Threshold:** max CRAP {gate.MaxCrap} — CRAP(m) = comp² · (1 − cov)³ + comp"));
        if (gate.Outcome is GateOutcome.NoData)
        {
            sb.AppendLine();
            sb.AppendLine($"> **No verdict:** {gate.Reason}.");
            sb.AppendLine();   // close the blockquote — same CommonMark lazy-continuation fix as MarkdownFormatter
        }

        var rows = WorstFirst(report, top);
        if (rows.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("| Method | Comp | Cov % | CRAP |");
            sb.AppendLine("|--------|-----:|------:|-----:|");
            foreach (var m in rows)
                sb.AppendLine(Invariant(
                    $"| `{m.Method}` | {m.Complexity} | {m.Coverage * 100:F1}% | {m.Score:F1}{(CrapAnalysis.Exceeds(m.Score, gate.MaxCrap) ? " ❌" : "")} |"));
            if (top is { } t && report.Methods.Count > t)
            {
                sb.AppendLine();
                sb.AppendLine(Invariant($"_… {report.Methods.Count - t} more methods below (top {t} shown; the gate evaluates all)._"));
            }
        }

        if (report.Unscored.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Unscored methods (no complexity source)");
            sb.AppendLine();
            foreach (var u in report.Unscored)
                sb.AppendLine($"- `{u.Method}` — {u.Reason}");
        }

        if (report.UnmatchedMetricsMembers.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Unmatched metrics members");
            sb.AppendLine();
            foreach (var name in report.UnmatchedMetricsMembers)
                sb.AppendLine($"- `{name}`");
        }

        // Backticked one-line verdict CI logs grep for — rendered here, from the same gate as
        // badge and rows, never spliced on by callers. Same shape as the check summary.
        sb.AppendLine();
        sb.AppendLine($"`{gate}`");
        return sb.ToString();
    }

    /// <summary>
    /// Wire shape follows <see cref="JsonFormatter"/>'s conventions: camelCase, two-space
    /// indentation, absent key == clean (empty lists omitted), hand-written tokens
    /// (no reflection — AOT/trim safe). Percentages are rounded to two decimals like the
    /// coverage JSON; the raw 0..1 ratio is recoverable from the hit counts upstream.
    /// </summary>
    public static string FormatJson(CrapReport report, CrapGateResult gate, int? top = null)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();

            writer.WriteStartObject("gate");
            writer.WriteString("outcome", gate.Outcome.ToString().ToLowerInvariant());
            writer.WriteNumber("maxCrap", gate.MaxCrap);
            writer.WriteNumber("scoredMethods", gate.ScoredMethods);
            writer.WriteNumber("aboveThreshold", gate.AboveThreshold);
            if (gate.WorstScore is { } worst) writer.WriteNumber("worstScore", Round(worst));
            writer.WriteString("reason", gate.Reason);
            writer.WriteEndObject();

            writer.WriteStartArray("methods");
            foreach (var m in WorstFirst(report, top))
            {
                writer.WriteStartObject();
                writer.WriteString("method", m.Method);
                writer.WriteString("file", m.File);
                writer.WriteNumber("line", m.StartLine);
                writer.WriteNumber("complexity", m.Complexity);
                writer.WriteNumber("coverage", Round(m.Coverage * 100));
                writer.WriteNumber("crap", Round(m.Score));
                writer.WriteBoolean("aboveThreshold", CrapAnalysis.Exceeds(m.Score, gate.MaxCrap));
                writer.WriteString("complexitySource",
                    m.ComplexitySource is CrapComplexitySource.CoverageReport ? "coverageReport" : "metricsFile");
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            if (report.Unscored.Count > 0)
            {
                writer.WriteStartArray("unscored");
                foreach (var u in report.Unscored)
                {
                    writer.WriteStartObject();
                    writer.WriteString("method", u.Method);
                    writer.WriteString("file", u.File);
                    writer.WriteString("reason", u.Reason);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }

            if (report.UnmatchedMetricsMembers.Count > 0)
            {
                writer.WriteStartArray("unmatchedMetricsMembers");
                foreach (var name in report.UnmatchedMetricsMembers)
                    writer.WriteStringValue(name);
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// The single worst-first ordering: descending score, ties keep document order (stable
    /// sort). <c>top</c> truncates the DISPLAY in every format; the gate always evaluates the
    /// full set.
    /// </summary>
    private static List<CrapMethod> WorstFirst(CrapReport report, int? top)
    {
        IEnumerable<CrapMethod> ordered = report.Methods.OrderByDescending(static m => m.Score);
        if (top is { } t) ordered = ordered.Take(t);
        return ordered.ToList();
    }

    private static void AppendHonestyTrailers(StringBuilder sb, CrapReport report, AnsiPen pen)
    {
        if (report.Unscored.Count > 0)
        {
            sb.AppendLine(pen.Dim($"Unscored (no complexity source): {report.Unscored.Count}"));
            foreach (var u in report.Unscored)
                sb.AppendLine(pen.Dim($"  {u.Method} - {u.Reason}"));
        }

        if (report.UnmatchedMetricsMembers.Count > 0)
        {
            sb.AppendLine(pen.Dim($"Unmatched metrics members: {report.UnmatchedMetricsMembers.Count}"));
            foreach (var name in report.UnmatchedMetricsMembers)
                sb.AppendLine(pen.Dim($"  {name}"));
        }
    }

    private static double Round(double value) => Math.Round(value, 2, MidpointRounding.ToEven);
}
