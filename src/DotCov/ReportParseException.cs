using System.Xml;

namespace DotCov;

/// <summary>
/// A report named by <see cref="ReportInput.SourceName"/> is not well-formed XML or exceeds the
/// character cap. Carries the source name, the XML coordinates and the original
/// <see cref="XmlException"/> as structured data; callers at the output boundary decide how to
/// render them. <see cref="Exception.Message"/> is the original reader message, unmodified.
/// </summary>
public sealed class ReportParseException : Exception
{
    public ReportParseException(string sourceName, XmlException inner)
        : base(inner.Message, inner)
    {
        SourceName = sourceName;
        LineNumber = inner.LineNumber;
        LinePosition = inner.LinePosition;
    }

    /// <summary>The file path or caller-supplied label of the offending input.</summary>
    public string SourceName { get; }

    /// <summary>1-based line in the XML document, or 0 when unknown.</summary>
    public int LineNumber { get; }

    /// <summary>1-based column in the XML document, or 0 when unknown.</summary>
    public int LinePosition { get; }

    /// <summary>The original reader failure; never null.</summary>
    public new XmlException InnerException => (XmlException)base.InnerException!;
}
