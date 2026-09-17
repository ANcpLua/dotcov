namespace DotCov;

/// <summary>
/// One Cobertura document to parse: a name for diagnostics and a factory that opens a fresh
/// readable stream. Streams opened through <see cref="OpenStream"/> belong to the parser, which
/// disposes them; streams a caller hands to the parser directly stay the caller's.
/// </summary>
public sealed class ReportInput
{
    private readonly Func<Stream> _open;

    private ReportInput(string sourceName, Func<Stream> open)
    {
        SourceName = sourceName;
        _open = open;
    }

    /// <summary>The file path, or the caller-supplied label for in-memory input. Used verbatim in diagnostics.</summary>
    public string SourceName { get; }

    /// <summary>Open a new stream positioned at the start of the document.</summary>
    public Stream OpenStream() => _open();

    public static ReportInput FromFile(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return new ReportInput(path, () => File.OpenRead(path));
    }

    public static ReportInput FromBytes(string sourceName, byte[] bytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(sourceName);
        ArgumentNullException.ThrowIfNull(bytes);
        return new ReportInput(sourceName, () => new MemoryStream(bytes, writable: false));
    }

    public override string ToString() => SourceName;
}
