using System.Buffers;
using System.Collections.Frozen;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Xml;
using static System.FormattableString;

namespace DotCov.Formatters;

/// <summary>
/// The coverage badge, drawn from an evaluated <see cref="GateResult"/> rather than from a rate:
/// the colour is the gate's outcome, so a branch rate below its minimum turns the badge red even
/// while the line rate reads <c>100.0%</c>, and an unmeasured or disabled gate never looks green.
/// <see cref="Svg"/> draws the image a README embeds; <see cref="ShieldsJson"/> is the same badge
/// in the shields.io endpoint schema. Both are pure and invariant-formatted.
/// </summary>
public static class BadgeFormatter
{
    /// <summary>The file name <c>--badge-dir</c> and the Fallout component write <see cref="Svg"/> to.</summary>
    public const string SvgFileName = "coverage-badge.svg";

    /// <summary>The file name <c>--badge-dir</c> and the Fallout component write <see cref="ShieldsJson"/> to.</summary>
    public const string JsonFileName = "coverage-badge.json";

    private const string Label = "Coverage";

    /// <summary>
    /// Verdana 11px advance widths in tenths of a pixel, read from Verdana.ttf, for every character
    /// a badge can carry: the label, rates (digits, <c>.</c>, <c>%</c>, <c>/</c>, space, and
    /// <c>n/a</c>), <c>no data</c>, and <c>disabled</c>. A width only sizes the box; the drawn text
    /// is pinned to it with <c>textLength</c>, so an estimate can shift glyph spacing but never clip.
    /// </summary>
    private static readonly FrozenDictionary<char, int> Widths = new Dictionary<char, int>
    {
        ['0'] = 70, ['1'] = 70, ['2'] = 70, ['3'] = 70, ['4'] = 70,
        ['5'] = 70, ['6'] = 70, ['7'] = 70, ['8'] = 70, ['9'] = 70,
        ['.'] = 40, ['%'] = 118, ['/'] = 50, [' '] = 39,
        ['C'] = 77, ['a'] = 66, ['b'] = 69, ['d'] = 69, ['e'] = 66, ['g'] = 69, ['i'] = 30,
        ['l'] = 30, ['n'] = 70, ['o'] = 67, ['r'] = 47, ['s'] = 57, ['t'] = 43, ['v'] = 65,
    }.ToFrozenDictionary();

    /// <summary>
    /// The badge as an SVG in the shields flat-square geometry: 20px high, Verdana 11px, the label
    /// on grey and the message on the outcome's colour. The <c>title</c> (hover text) and
    /// <c>aria-label</c> carry the full <see cref="GateResult.ToString"/> verdict.
    /// </summary>
    /// <exception cref="ArgumentException">The message holds a character the width table lacks, e.g. a NaN rate.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="GateResult.Outcome"/> is not a defined outcome.</exception>
    /// <exception cref="XmlException"><see cref="GateResult.Reason"/> holds a character XML cannot carry.</exception>
    public static string Svg(GateResult gate)
    {
        var (message, messageText, hex, _) = Describe(gate);
        var labelText = Measure(Label);

        // Box widths in pixels: the text rounded up, plus 5px of padding on either side. Text is
        // drawn at ten times scale, so its x is the box centre in tenths of a pixel.
        var labelWidth = BoxWidth(labelText);
        var messageWidth = BoxWidth(messageText);
        // The verdict carries the caller's Reason and is escaped; the message needs no escaping,
        // since Measure admits only the width table's characters.
        var verdict = Xml($"{Label}: {gate}");

        return Invariant($"""<svg xmlns="http://www.w3.org/2000/svg" width="{labelWidth + messageWidth}" height="20" role="img" aria-label="{verdict}"><title>{verdict}</title><g shape-rendering="crispEdges"><rect width="{labelWidth}" height="20" fill="#555"/><rect x="{labelWidth}" width="{messageWidth}" height="20" fill="{hex}"/></g><g fill="#fff" text-anchor="middle" font-family="Verdana,Geneva,DejaVu Sans,sans-serif" text-rendering="geometricPrecision" font-size="110"><text x="{labelWidth * 5}" y="140" textLength="{labelText}" transform="scale(.1)">{Label}</text><text x="{labelWidth * 10 + messageWidth * 5}" y="140" textLength="{messageText}" transform="scale(.1)">{message}</text></g></svg>""");
    }

    /// <summary>
    /// The badge in the shields.io endpoint schema, for
    /// <c>https://img.shields.io/endpoint?url=…</c>: label, message, and the shields colour name
    /// of the outcome.
    /// </summary>
    /// <exception cref="ArgumentException">The message holds a character the width table lacks, e.g. a NaN rate.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="GateResult.Outcome"/> is not a defined outcome.</exception>
    public static string ShieldsJson(GateResult gate)
    {
        var (message, _, _, color) = Describe(gate);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("label", Label);
            writer.WriteString("message", message);
            writer.WriteString("color", color);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Message, its width, and colours of an outcome. A measured gate shows its line rate, and its
    /// branch rate too when a branch minimum was armed, so a badge that judges two numbers shows
    /// both. Rates come from <see cref="GateResult"/>'s own formatting, so a FAIL never shows a
    /// rate that reads as meeting the minimum it missed. Measured for both renderings, so the SVG
    /// and the JSON reject the same gates.
    /// </summary>
    private static (string Message, int Width, string Hex, string Color) Describe(GateResult gate)
    {
        var (message, hex, color) = gate.Outcome switch
        {
            GateOutcome.Pass => (Rates(gate), "#4c1", "brightgreen"),
            GateOutcome.Fail => (Rates(gate), "#e05d44", "red"),
            GateOutcome.NoData => ("no data", "#9f9f9f", "lightgrey"),
            GateOutcome.Disabled => ("disabled", "#dfb317", "yellow"),
            _ => throw new ArgumentOutOfRangeException(nameof(gate), gate.Outcome, "Undefined gate outcome."),
        };
        return (message, Measure(message), hex, color);
    }

    private static string Rates(GateResult gate) =>
        gate.MinBranchPercent > 0 ? $"{gate.LineRateText} / {gate.BranchRateText}" : gate.LineRateText;

    /// <summary>Text width in tenths of a pixel; throws for a character the table lacks rather than guessing.</summary>
    private static int Measure(string text)
    {
        var width = 0;
        foreach (var c in text)
            width += Widths.TryGetValue(c, out var w)
                ? w
                : throw new ArgumentException($"The badge cannot measure '{c}' in '{text}'.", nameof(text));
        return width;
    }

    private static int BoxWidth(int textTenths) => (textTenths + 9) / 10 + 10;

    /// <summary>Escaped for an XML attribute or text node; a character XML cannot carry throws.</summary>
    private static string Xml(string text) => SecurityElement.Escape(XmlConvert.VerifyXmlChars(text));
}
