using System.Text;
using System.Xml.Linq;

namespace DotCov.Tests.Infrastructure;

/// <summary>
/// Fluent Cobertura XML builder for parser tests. Lets you spell out a coverage document
/// without copy-pasting fixture files for every edge case.
///
/// <code>
/// var xml = Cobertura.NewDoc()
///     .AddClass("src/A.cs", c => c
///         .Line(1, hits: 3)
///         .Line(2, hits: 0)
///         .Branch(10, "75% (3/4)"))
///     .ToString();
/// </code>
/// </summary>
public sealed class Cobertura
{
    private readonly List<XElement> _classes = [];

    public static Cobertura NewDoc() => new();

    public Cobertura AddClass(string filename, Action<ClassBuilder> configure)
    {
        var cls = new XElement("class",
            new XAttribute("name", filename.Replace('/', '.')),
            new XAttribute("filename", filename));
        var builder = new ClassBuilder(cls);
        configure(builder);
        _classes.Add(cls);
        return this;
    }

    public override string ToString() => Encoding.UTF8.GetString(ToBytes());

    public Stream ToStream() => new MemoryStream(ToBytes());

    public byte[] ToBytes()
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("coverage",
                new XAttribute("line-rate", "0"),
                new XAttribute("branch-rate", "0"),
                new XAttribute("version", "1.0"),
                new XAttribute("timestamp", "1700000000"),
                new XElement("packages",
                    new XElement("package",
                        new XAttribute("name", "Tests"),
                        new XElement("classes", _classes)))));

        // Save through a real UTF-8 stream so the encoding declaration matches the byte payload
        // — saving via StringWriter would emit utf-16 in the declaration and break the reader.
        using var ms = new MemoryStream();
        doc.Save(ms);
        return ms.ToArray();
    }

    public CoverageReport Parse() => CoberturaParser.Parse(ToStream());

    public sealed class ClassBuilder(XElement cls)
    {
        private XElement? _lines;

        public ClassBuilder Line(int number, int hits)
        {
            Lines().Add(new XElement("line",
                new XAttribute("number", number),
                new XAttribute("hits", hits),
                new XAttribute("branch", "false")));
            return this;
        }

        public ClassBuilder Branch(int number, string conditionCoverage, int hits = 1)
        {
            Lines().Add(new XElement("line",
                new XAttribute("number", number),
                new XAttribute("hits", hits),
                new XAttribute("branch", "true"),
                new XAttribute("condition-coverage", conditionCoverage)));
            return this;
        }

        public ClassBuilder MalformedLine(string number, string hits)
        {
            Lines().Add(new XElement("line",
                new XAttribute("number", number),
                new XAttribute("hits", hits),
                new XAttribute("branch", "false")));
            return this;
        }

        private XElement Lines() => _lines ??= CreateLines();

        private XElement CreateLines()
        {
            var lines = new XElement("lines");
            cls.Add(lines);
            return lines;
        }
    }
}
