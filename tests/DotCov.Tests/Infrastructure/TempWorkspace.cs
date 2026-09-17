namespace DotCov.Tests.Infrastructure;

/// <summary>
/// A per-test temporary directory, deleted on dispose whatever the test outcome. Paths are
/// returned through <see cref="Path.GetFullPath(string)"/> so assertions compare against the
/// same spelling the resolver's enumeration produces (relevant on Windows, where a relative
/// segment with '/' would otherwise keep mixed separators).
/// </summary>
public sealed class TempWorkspace : IDisposable
{
    private TempWorkspace(string root) => Root = root;

    public string Root { get; }

    public static TempWorkspace Create(string prefix = "dotcov-test-") =>
        new(Directory.CreateTempSubdirectory(prefix).FullName);

    public string PathOf(string relative) => Path.GetFullPath(Path.Combine(Root, relative));

    public string Write(string relative, byte[] bytes)
    {
        var full = PathOf(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
        return full;
    }

    public string Write(string relative, Cobertura document) => Write(relative, document.ToBytes());

    public string Write(string relative, string text) => Write(relative, System.Text.Encoding.UTF8.GetBytes(text));

    public string CreateDirectory(string relative)
    {
        var full = PathOf(relative);
        Directory.CreateDirectory(full);
        return full;
    }

    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}
