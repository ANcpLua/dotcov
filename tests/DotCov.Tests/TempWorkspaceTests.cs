using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

[Category("Integration")]
public sealed class TempWorkspaceTests
{
    private readonly TempWorkspace _workspace = TempWorkspace.Create();

    [After(Test)]
    public void CleanupDirectory()
    {
        // Cleanup must still work if the Dispose implementation under test is broken.
        if (Directory.Exists(_workspace.Root)) Directory.Delete(_workspace.Root, recursive: true);
    }

    [Test]
    public async Task PrepareFile_CreatesParentsWithoutCreatingTheFile()
    {
        var path = _workspace.PrepareFile("nested/reports/coverage.xml");

        await Assert.That(path).IsEqualTo(Path.GetFullPath(Path.Combine(_workspace.Root, "nested/reports/coverage.xml")));
        await Assert.That(Directory.Exists(Path.GetDirectoryName(path))).IsTrue();
        await Assert.That(File.Exists(path)).IsFalse();
    }

    [Test]
    public async Task Dispose_RemovesNestedFilesAndDirectory()
    {
        var root = _workspace.Root;
        _workspace.Write("nested/report.xml", "report");

        _workspace.Dispose();

        await Assert.That(Directory.Exists(root)).IsFalse();
    }

    [Test]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        _workspace.Dispose();

        _workspace.Dispose();
    }
}
