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
[TestClass]
public sealed class BadgeFormatterTests
{
    private static readonly XNamespace Svg = "http://www.w3.org/2000/svg";
    public static IEnumerable<(GateResult Gate, string Message, string Hex, string Color)> Outcomes()
    {
        // Line minimum only: the badge shows the one number the gate judged.
        yield return (Reports.Single("a.cs", hit: 1, total: 2).Evaluate(40), "50.0%", "#4c1", "brightgreen");
        // A branch minimum is armed: two numbers judged, two shown.
        yield return (Reports.Single("a.cs", hit: 10, total: 10, bHit: 1, bTotal: 2).Evaluate(40, 40), "100.0% / 50.0%", "#4c1", "brightgreen");
        // 1999/2500 = 79.96% against 80: floored like the verdict, never a 80.0% that reads as met.
        yield return (Reports.Single("a.cs", hit: 1999, total: 2500).Evaluate(80), "79.9%", "#e05d44", "red");
        // The case the bash badge got wrong: lines at 100%, branches short of their minimum.
        yield return (Reports.Single("a.cs", hit: 10, total: 10, bHit: 1999, bTotal: 2500).Evaluate(100, 80), "100.0% / 79.9%", "#e05d44", "red");
        yield return (CoverageReport.Empty.Evaluate(80), "no data", "#9f9f9f", "lightgrey");
        // Lines measured, but a branch minimum against a report without branch data.
        yield return (Reports.Single("a.cs", hit: 10, total: 10).Evaluate(80, 50), "no data", "#9f9f9f", "lightgrey");
        yield return (Reports.Single("a.cs", hit: 10, total: 10).Evaluate(0), "disabled", "#dfb317", "yellow");
    }

    [TestMethod]
    [DynamicData(nameof(Outcomes))]
    public void Outcome_SetsMessageAndColour_InBothRenderings(GateResult gate, string message, string hex, string color)
    {
        var svg = XDocument.Parse(BadgeFormatter.Svg(gate)).Root!;
        using var json = JsonDocument.Parse(BadgeFormatter.ShieldsJson(gate));
        var rects = svg.Descendants(Svg + "rect").ToList();
        var texts = svg.Descendants(Svg + "text").ToList();
        Assert.AreEqual(hex, rects[1].Attribute("fill")!.Value);
        Assert.AreEqual("Coverage", texts[0].Value);
        Assert.AreEqual(message, texts[1].Value);
        Assert.AreEqual(1, json.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual("Coverage", json.RootElement.GetProperty("label").GetString());
        Assert.AreEqual(message, json.RootElement.GetProperty("message").GetString());
        Assert.AreEqual(color, json.RootElement.GetProperty("color").GetString());
    }

    [TestMethod]
    [DynamicData(nameof(Outcomes))]
    public void Hover_And_ScreenReaders_GetTheFullVerdict(GateResult gate, string message, string hex, string color)
    {
        var svg = XDocument.Parse(BadgeFormatter.Svg(gate)).Root!;
        Assert.AreEqual($"Coverage: {gate}", svg.Element(Svg + "title")!.Value);
        Assert.AreEqual($"Coverage: {gate}", svg.Attribute("aria-label")!.Value);
        Assert.AreEqual("img", svg.Attribute("role")!.Value);
    }

    [TestMethod]
    [DynamicData(nameof(Outcomes))]
    public void Boxes_HoldTheirText_AndTileTheBadge(GateResult gate, string message, string hex, string color)
    {
        var svg = XDocument.Parse(BadgeFormatter.Svg(gate)).Root!;
        var rects = svg.Descendants(Svg + "rect").Select(static r => Int(r, "width")).ToList();
        var texts = svg.Descendants(Svg + "text").ToList();
        Assert.AreEqual(20, Int(svg, "height"));
        Assert.AreEqual(rects[0] + rects[1], Int(svg, "width"));
        for (var i = 0; i < 2; i++)
        {
            // textLength pins the drawn width (tenths of a pixel at scale .1); 5px padding each side.
            var textLength = Int(texts[i], "textLength");
            Assert.IsGreaterThan(0, textLength);
            Assert.IsGreaterThanOrEqualTo(textLength + 100, rects[i] * 10);
            Assert.IsLessThan(textLength + 110, rects[i] * 10);
        }

        // Each text is centred in its own box.
        Assert.AreEqual(rects[0] * 5, Int(texts[0], "x"));
        Assert.AreEqual(rects[0] * 10 + rects[1] * 5, Int(texts[1], "x"));
    }

    [TestMethod]
    public void Svg_PinsTheFlatSquareDocument()
    {
        var gate = Reports.Single("a.cs", hit: 10, total: 10).Evaluate(80);
        Assert.AreEqual("""<svg xmlns="http://www.w3.org/2000/svg" width="117" height="20" role="img" aria-label="Coverage: PASS: line 100.0% (min 80%), branch n/a (min 0%) - thresholds met"><title>Coverage: PASS: line 100.0% (min 80%), branch n/a (min 0%) - thresholds met</title><g shape-rendering="crispEdges"><rect width="63" height="20" fill="#555"/><rect x="63" width="54" height="20" fill="#4c1"/></g><g fill="#fff" text-anchor="middle" font-family="Verdana,Geneva,DejaVu Sans,sans-serif" text-rendering="geometricPrecision" font-size="110"><text x="315" y="140" textLength="523" transform="scale(.1)">Coverage</text><text x="900" y="140" textLength="438" transform="scale(.1)">100.0%</text></g></svg>""", BadgeFormatter.Svg(gate));
    }

    [TestMethod]
    public void ShieldsJson_PinsTheEndpointDocument()
    {
        var gate = Reports.Single("a.cs", hit: 10, total: 10, bHit: 1, bTotal: 2).Evaluate(40, 40);
        Assert.AreEqual("""{"schemaVersion":1,"label":"Coverage","message":"100.0% / 50.0%","color":"brightgreen"}""", BadgeFormatter.ShieldsJson(gate));
    }

    [TestMethod]
    public void CommaDecimalCulture_KeepsDotDecimals()
    {
        var gate = Reports.Single("a.cs", hit: 1999, total: 2500).Evaluate(80);
        var svg = CultureScope.RenderWithCommaDecimal(() => BadgeFormatter.Svg(gate));
        var json = CultureScope.RenderWithCommaDecimal(() => BadgeFormatter.ShieldsJson(gate));
        Assert.Contains(">79.9%</text>", svg);
        Assert.DoesNotContain("79,9", svg);
        Assert.Contains("\"79.9%\"", json);
    }

    [TestMethod]
    public void Reason_IsEscaped_SoTheSvgStaysWellFormed()
    {
        var gate = new GateResult(GateOutcome.Pass, 1, null, 80, 0, """<b> & "quoted" 'single'""");
        var svg = XDocument.Parse(BadgeFormatter.Svg(gate)).Root!;
        Assert.AreEqual($"Coverage: {gate}", svg.Element(Svg + "title")!.Value);
        Assert.AreEqual($"Coverage: {gate}", svg.Attribute("aria-label")!.Value);
    }

    [TestMethod]
    public void Reason_WithACharacterXmlCannotCarry_Throws()
    {
        var gate = new GateResult(GateOutcome.Pass, 1, null, 80, 0, "bell \u0007");
        var ex = Assert.ThrowsExactly<XmlException>(() => BadgeFormatter.Svg(gate));
        Assert.Contains("0x07", ex.Message);
    }

    [TestMethod]
    public void UnmeasurableCharacter_Throws_InBothRenderings()
    {
        // A NaN rate prints "NaN%": no width for 'N', so no guessed box that could clip.
        var gate = new GateResult(GateOutcome.Fail, double.NaN, null, 80, 0, "line coverage below threshold");
        var svg = Assert.ThrowsExactly<ArgumentException>(() => BadgeFormatter.Svg(gate));
        var json = Assert.ThrowsExactly<ArgumentException>(() => BadgeFormatter.ShieldsJson(gate));
        Assert.Contains("'N' in 'NaN%'", svg.Message);
        Assert.Contains("'N' in 'NaN%'", json.Message);
    }

    [TestMethod]
    public void UndefinedOutcome_Throws_InBothRenderings()
    {
        var gate = new GateResult((GateOutcome)42, 1, 1, 80, 0, "thresholds met");
        var svg = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => BadgeFormatter.Svg(gate));
        var json = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => BadgeFormatter.ShieldsJson(gate));
        Assert.AreEqual((GateOutcome)42, svg.ActualValue);
        Assert.AreEqual((GateOutcome)42, json.ActualValue);
    }

    private static int Int(XElement element, string attribute) => int.Parse(element.Attribute(attribute)!.Value, CultureInfo.InvariantCulture);
}
