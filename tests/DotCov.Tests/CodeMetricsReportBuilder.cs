using System.Xml.Linq;

namespace DotCov.Tests;

/// <summary>
/// Builds Microsoft.CodeAnalysis.Metrics XML in the exact shape <c>Metrics.exe</c>'s
/// <c>MetricsOutputWriter</c> writes: namespaces named with <c>ToDisplayString()</c> (so the
/// global namespace is <c>&lt;global namespace&gt;</c>), types and members named with
/// <c>SymbolDisplayFormat.MinimallyQualifiedFormat</c>, per-node <c>Metrics</c>, and
/// property/event <c>Accessors</c>. Display strings are passed unescaped, the way Roslyn
/// produces them; XML escaping happens here.
/// </summary>
internal sealed class CodeMetricsReportBuilder
{
    private readonly XElement _namespaces = new("Namespaces");

    public static CodeMetricsReportBuilder Create() => new();

    public CodeMetricsReportBuilder Namespace(string name, Action<TypeListBuilder> types)
    {
        var typeList = new XElement("Types");
        types(new TypeListBuilder(typeList));
        _namespaces.Add(new XElement("Namespace", new XAttribute("Name", name), Metrics(888), typeList));
        return this;
    }

    public static IReadOnlyList<CodeMetricsMember> ParseType(string ns, string type, Action<MemberListBuilder> members) =>
        Create().Namespace(ns, t => t.Type(type, members)).Parse();

    public byte[] ToBytes()
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("CodeMetricsReport", new XAttribute("Version", "1.0"),
                new XElement("Targets",
                    new XElement("Target", new XAttribute("Name", "Probe.csproj"),
                        new XElement("Assembly", new XAttribute("Name", "Probe, Version=1.0.0.0"),
                            Metrics(999),
                            _namespaces)))));
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    public IReadOnlyList<CodeMetricsMember> Parse()
    {
        using var stream = new MemoryStream(ToBytes());
        return CodeMetricsReader.Parse(stream);
    }

    internal static XElement Metrics(int complexity) =>
        new("Metrics",
            new XElement("Metric", new XAttribute("Name", "MaintainabilityIndex"), new XAttribute("Value", "90")),
            new XElement("Metric", new XAttribute("Name", "CyclomaticComplexity"), new XAttribute("Value", complexity)),
            new XElement("Metric", new XAttribute("Name", "ClassCoupling"), new XAttribute("Value", "2")));

    internal sealed class TypeListBuilder(XElement types)
    {
        public TypeListBuilder Type(string name, Action<MemberListBuilder> members)
        {
            var list = new XElement("Members");
            members(new MemberListBuilder(list));
            types.Add(new XElement("NamedType", new XAttribute("Name", name), Metrics(777), list));
            return this;
        }
    }

    internal sealed class MemberListBuilder(XElement members)
    {
        public MemberListBuilder Method(string display, int complexity, int? executableLines = null) =>
            Member("Method", display, complexity, null, executableLines);

        public MemberListBuilder Property(string display, int complexity, Action<MemberListBuilder>? accessors = null) =>
            Member("Property", display, complexity, accessors);

        private MemberListBuilder Member(string element, string display, int complexity, Action<MemberListBuilder>? accessors,
            int? executableLines = null)
        {
            var member = new XElement(element,
                new XAttribute("Name", display),
                new XAttribute("File", "/src/Probe.cs"),
                new XAttribute("Line", "1"),
                Metrics(complexity));
            if (executableLines is { } lines)
                member.Element("Metrics")!.Add(new XElement("Metric",
                    new XAttribute("Name", "ExecutableLines"), new XAttribute("Value", lines)));
            if (accessors is not null)
            {
                var list = new XElement("Accessors");
                accessors(new MemberListBuilder(list));
                member.Add(list);
            }
            members.Add(member);
            return this;
        }
    }
}
