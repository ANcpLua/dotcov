using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using DotCov.Formatters;
using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

/// <summary>
/// The badge is drawn from the evaluated gate, not from a rate: its colour is the outcome, a FAIL
/// floors like the verdict, a branch minimum shows the branch rate, and an unmeasured or disabled
/// gate never looks green. Pinned through the public renderer, SVG and shields JSON alike.
/// </summary>
public sealed class BadgeFormatterTests
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";

    public static IEnumerable<(GateResult Gate, string Message, string Hex, string Color)> Outcomes()
    {
        // Line minimum only: the badge shows the one number the gate judged.
        yield return (Reports.Single("a.cs", hit: 1, total: 2).Evaluate(40), "50.0%", "#4c1", "brightgreen");
        // A branch minimum is armed: two numbers judged, two shown.
        yield return (Reports.Single("a.cs", hit: 10, total: 10, bHit: 1, bTotal: 2).Evaluate(40, 40),
            "100.0% / 50.0%", "#4c1", "brightgreen");
        // 1999/2500 = 79.96% against 80: floored like the verdict, never a 80.0% that reads as met.
        yield return (Reports.Single("a.cs", hit: 1999, total: 2500).Evaluate(80), "79.9%", "#e05d44", "red");
        // The case the bash badge got wrong: lines at 100%, branches short of their minimum.
        yield return (Reports.Single("a.cs", hit: 10, total: 10, bHit: 1999, bTotal: 2500).Evaluate(100, 80),
            "100.0% / 79.9%", "#e05d44", "red");
        yield return (CoverageReport.Empty.Evaluate(80), "no data", "#9f9f9f", "lightgrey");
        // Lines measured, but a branch minimum against a report without branch data.
        yield return (Reports.Single("a.cs", hit: 10, total: 10).Evaluate(80, 50), "no data", "#9f9f9f", "lightgrey");
        yield return (Reports.Single("a.cs", hit: 10, total: 10).Evaluate(0, 0), "disabled", "#dfb317", "yellow");
    }

    [Test]
    [MethodDataSource(nameof(Outcomes))]
    public async Task Outcome_SetsMessageAndColour_InBothRenderings(GateResult gate, string message, string hex, string color)
    {
        var svg = XDocument.Parse(BadgeFormatter.Svg(gate)).Root!;
        using var json = JsonDocument.Parse(BadgeFormatter.ShieldsJson(gate));

        var rects = svg.Descendants(Svg + "rect").ToList();
        var texts = svg.Descendants(Svg + "text").ToList();
        await Assert.That(rects[1].Attribute("fill")!.Value).IsEqualTo(hex);
        await Assert.That(texts[0].Value).IsEqualTo("Coverage");
        await Assert.That(texts[1].Value).IsEqualTo(message);
        await Assert.That(json.RootElement.GetProperty("schemaVersion").GetInt32()).IsEqualTo(1);
        await Assert.That(json.RootElement.GetProperty("label").GetString()).IsEqualTo("Coverage");
        await Assert.That(json.RootElement.GetProperty("message").GetString()).IsEqualTo(message);
        await Assert.That(json.RootElement.GetProperty("color").GetString()).IsEqualTo(color);
    }

    [Test]
    [MethodDataSource(nameof(Outcomes))]
    public async Task Hover_And_ScreenReaders_GetTheFullVerdict(GateResult gate, string message, string hex, string color)
    {
        var svg = XDocument.Parse(BadgeFormatter.Svg(gate)).Root!;

        await Assert.That(svg.Element(Svg + "title")!.Value).IsEqualTo($"Coverage: {gate}");
        await Assert.That(svg.Attribute("aria-label")!.Value).IsEqualTo($"Coverage: {gate}");
        await Assert.That(svg.Attribute("role")!.Value).IsEqualTo("img");
    }

    [Test]
    [MethodDataSource(nameof(Outcomes))]
    public async Task Boxes_HoldTheirText_AndTileTheBadge(GateResult gate, string message, string hex, string color)
    {
        var svg = XDocument.Parse(BadgeFormatter.Svg(gate)).Root!;
        var rects = svg.Descendants(Svg + "rect").Select(static r => Int(r, "width")).ToList();
        var texts = svg.Descendants(Svg + "text").ToList();

        await Assert.That(Int(svg, "height")).IsEqualTo(20);
        await Assert.That(Int(svg, "width")).IsEqualTo(rects[0] + rects[1]);
        for (var i = 0; i < 2; i++)
        {
            // textLength pins the drawn width (tenths of a pixel at scale .1); 5px padding each side.
            var textLength = Int(texts[i], "textLength");
            await Assert.That(textLength).IsGreaterThan(0);
            await Assert.That(rects[i] * 10).IsGreaterThanOrEqualTo(textLength + 100);
            await Assert.That(rects[i] * 10).IsLessThan(textLength + 110);
        }

        // Each text is centred in its own box.
        await Assert.That(Int(texts[0], "x")).IsEqualTo(rects[0] * 5);
        await Assert.That(Int(texts[1], "x")).IsEqualTo(rects[0] * 10 + rects[1] * 5);
    }

    [Test]
    public async Task Svg_PinsTheFlatSquareDocument()
    {
        var gate = Reports.Single("a.cs", hit: 10, total: 10).Evaluate(80);

        await Assert.That(BadgeFormatter.Svg(gate)).IsEqualTo(
            """<svg xmlns="http://www.w3.org/2000/svg" width="117" height="20" role="img" aria-label="Coverage: PASS: line 100.0% (min 80%), branch n/a (min 0%) - thresholds met"><title>Coverage: PASS: line 100.0% (min 80%), branch n/a (min 0%) - thresholds met</title><g shape-rendering="crispEdges"><rect width="63" height="20" fill="#555"/><rect x="63" width="54" height="20" fill="#4c1"/></g><g fill="#fff" text-anchor="middle" font-family="Verdana,Geneva,DejaVu Sans,sans-serif" text-rendering="geometricPrecision" font-size="110"><text x="315" y="140" textLength="523" transform="scale(.1)">Coverage</text><text x="900" y="140" textLength="438" transform="scale(.1)">100.0%</text></g></svg>""");
    }

    [Test]
    public async Task ShieldsJson_PinsTheEndpointDocument()
    {
        var gate = Reports.Single("a.cs", hit: 10, total: 10, bHit: 1, bTotal: 2).Evaluate(40, 40);

        await Assert.That(BadgeFormatter.ShieldsJson(gate)).IsEqualTo(
            """{"schemaVersion":1,"label":"Coverage","message":"100.0% / 50.0%","color":"brightgreen"}""");
    }

    [Test]
    public async Task CommaDecimalCulture_KeepsDotDecimals()
    {
        var gate = Reports.Single("a.cs", hit: 1999, total: 2500).Evaluate(80);

        var svg = CultureScope.RenderWithCommaDecimal(() => BadgeFormatter.Svg(gate));
        var json = CultureScope.RenderWithCommaDecimal(() => BadgeFormatter.ShieldsJson(gate));

        await Assert.That(svg).Contains(">79.9%</text>");
        await Assert.That(svg).DoesNotContain("79,9");
        await Assert.That(json).Contains("\"79.9%\"");
    }

    [Test]
    public async Task Reason_IsEscaped_SoTheSvgStaysWellFormed()
    {
        var gate = new GateResult(GateOutcome.Pass, 1, null, 80, 0, """<b> & "quoted" 'single'""");

        var svg = XDocument.Parse(BadgeFormatter.Svg(gate)).Root!;

        await Assert.That(svg.Element(Svg + "title")!.Value).IsEqualTo($"Coverage: {gate}");
        await Assert.That(svg.Attribute("aria-label")!.Value).IsEqualTo($"Coverage: {gate}");
    }

    [Test]
    public async Task Reason_WithACharacterXmlCannotCarry_Throws()
    {
        var gate = new GateResult(GateOutcome.Pass, 1, null, 80, 0, "bell \u0007");

        var ex = Assert.ThrowsExactly<XmlException>(() => BadgeFormatter.Svg(gate));

        await Assert.That(ex.Message).Contains("0x07");
    }

    [Test]
    public async Task UnmeasurableCharacter_Throws_InBothRenderings()
    {
        // A NaN rate prints "NaN%": no width for 'N', so no guessed box that could clip.
        var gate = new GateResult(GateOutcome.Fail, double.NaN, null, 80, 0, "line coverage below threshold");

        var svg = Assert.ThrowsExactly<ArgumentException>(() => BadgeFormatter.Svg(gate));
        var json = Assert.ThrowsExactly<ArgumentException>(() => BadgeFormatter.ShieldsJson(gate));

        await Assert.That(svg.Message).Contains("'N' in 'NaN%'");
        await Assert.That(json.Message).Contains("'N' in 'NaN%'");
    }

    [Test]
    public async Task UndefinedOutcome_Throws_InBothRenderings()
    {
        var gate = new GateResult((GateOutcome)42, 1, 1, 80, 0, "thresholds met");

        var svg = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => BadgeFormatter.Svg(gate));
        var json = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => BadgeFormatter.ShieldsJson(gate));

        await Assert.That(svg.ActualValue).IsEqualTo((GateOutcome)42);
        await Assert.That(json.ActualValue).IsEqualTo((GateOutcome)42);
    }

    private static int Int(XElement element, string attribute) =>
        int.Parse(element.Attribute(attribute)!.Value, CultureInfo.InvariantCulture);
}
