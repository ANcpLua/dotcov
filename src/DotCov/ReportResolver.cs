namespace DotCov;

/// <summary>
/// The one place that turns a user-supplied path into the set of reports to parse. A file
/// resolves to itself; a directory is searched with a <see cref="ReportPattern"/>. The result
/// is ordered by ordinal path so merges are deterministic on every host. Whether the inputs
/// are read for file or method coverage is decided afterwards by the parser.
/// </summary>
public static class ReportResolver
{
    /// <summary>
    /// Resolve <paramref name="path"/> with the default pattern. Throws
    /// <see cref="FileNotFoundException"/> when nothing exists there; an existing directory
    /// with no matching report yields an empty list.
    /// </summary>
    public static IReadOnlyList<ReportInput> Resolve(string path) => Resolve(path, ReportPattern.Default);

    /// <inheritdoc cref="Resolve(string)"/>
    public static IReadOnlyList<ReportInput> Resolve(string path, ReportPattern pattern)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(pattern);

        if (File.Exists(path))
            return [ReportInput.FromFile(path)];
        if (Directory.Exists(path))
            return ResolveDirectory(path, pattern);

        throw new FileNotFoundException($"No file or directory at '{path}'.", path);
    }

    /// <summary>
    /// Every file under <paramref name="directory"/> whose name matches the pattern, ordinal
    /// by full path. Hidden and system directories are searched too: CI drops reports under
    /// dot-folders, and a report that exists but is not found is a silent NoData. Missing or
    /// inaccessible directories throw instead of being skipped.
    /// </summary>
    public static IReadOnlyList<ReportInput> ResolveDirectory(string directory, ReportPattern pattern)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(pattern);

        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"No directory at '{directory}'.");

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = pattern.Recursive,
            AttributesToSkip = FileAttributes.None,
            IgnoreInaccessible = false
        };

        return Directory.EnumerateFiles(directory, pattern.FileName, options)
            .Order(StringComparer.Ordinal)
            .Select(ReportInput.FromFile)
            .ToArray();
    }
}
