using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;
[TestClass]
[TestCategory("Integration")]
public sealed class TempWorkspaceTests
{
    private readonly TempWorkspace _workspace = TempWorkspace.Create();
    [TestCleanup]
    public void CleanupDirectory()
    {
        // Cleanup must still work if the Dispose implementation under test is broken.
        if (Directory.Exists(_workspace.Root))
            Directory.Delete(_workspace.Root, recursive: true);
    }

    [TestMethod]
    public void PrepareFile_CreatesParentsWithoutCreatingTheFile()
    {
        var path = _workspace.PrepareFile("nested/reports/coverage.xml");
        Assert.AreEqual(Path.GetFullPath(Path.Combine(_workspace.Root, "nested/reports/coverage.xml")), path);
        Assert.IsTrue(Directory.Exists(Path.GetDirectoryName(path)));
        Assert.IsFalse(File.Exists(path));
    }

    [TestMethod]
    public void Dispose_RemovesNestedFilesAndDirectory()
    {
        var root = _workspace.Root;
        _workspace.Write("nested/report.xml", "report");
        _workspace.Dispose();
        Assert.IsFalse(Directory.Exists(root));
    }

    [TestMethod]
    public void Dispose_RemovesReadOnlyNestedFilesAndDirectory()
    {
        var file = _workspace.Write("objects/ab/readonly-object", "object");
        File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
        try
        {
            _workspace.Dispose();
            Assert.IsFalse(Directory.Exists(_workspace.Root));
        }
        finally
        {
            // Let independent test cleanup remove the fixture even if disposal regresses.
            if (File.Exists(file)) File.SetAttributes(file, FileAttributes.Normal);
        }
    }

    [TestMethod]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        _workspace.Dispose();
        _workspace.Dispose();
    }
}
