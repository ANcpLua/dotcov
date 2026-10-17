using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

[TestClass]
public sealed class ParserStreamScenarioTests(TestContext testContext)
{
    public static IEnumerable<TestDataRow<int>> ReadChunkSizes =>
    [
        new(1) { ArgumentsDisplayName = "1-byte reads" },
        new(7) { ArgumentsDisplayName = "7-byte reads" },
        new(64) { ArgumentsDisplayName = "64-byte reads" }
    ];

    private static byte[] Document() => Cobertura.NewDoc()
        .WithSource("/répo/🧪")
        .AddClass("src/Größe.cs", "Example", c => c
            .Method("M", "()", "2", m => m.Line(1, 3).Line(2, 0))
            .Line(1, 3).Line(2, 0).Branch(3, "50% (1/2)"))
        .ToBytes();

    [TestMethod]
    [DynamicData(nameof(ReadChunkSizes))]
    public async Task ParseAsync_FragmentedNonSeekableInput_PreservesUnicodeAndMeasurements(int chunkSize)
    {
        await using var stream = new ScriptedReadStream(Document(), chunkSize, asyncOnly: true);

        var report = await CoberturaParser.ParseAsync(stream, ct: testContext.CancellationToken);

        var file = Assert.ContainsSingle(report.Files);
        Assert.AreEqual("/répo/🧪/src/Größe.cs", file.Path);
        Assert.AreEqual(3, file.LinesTotal);
        Assert.AreEqual(2, file.LinesHit);
        Assert.AreEqual(1, file.BranchesHit);
        Assert.AreEqual(2, file.BranchesTotal);
        Assert.IsEmpty(report.Warnings);
        Assert.IsTrue(stream.CanRead);
    }

    [TestMethod]
    [DynamicData(nameof(ReadChunkSizes))]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task ParseAsync_ReadPausesAndResumes_ReturnsCompleteCoverage(int chunkSize)
    {
        var cancellationToken = testContext.CancellationToken;
        var bytes = Document();
        await using var stream = new ScriptedReadStream(bytes, chunkSize, asyncOnly: true);
        stream.PauseAt(bytes.AsSpan().IndexOf("<line "u8) + 4);

        var parsing = CoberturaParser.ParseAsync(stream, ct: cancellationToken);
        CoverageReport report;
        try
        {
            await stream.Paused.WaitAsync(cancellationToken);
            Assert.IsFalse(parsing.IsCompleted);
        }
        finally
        {
            stream.Resume();
            report = await parsing;
        }

        var file = Assert.ContainsSingle(report.Files);
        Assert.AreEqual("/répo/🧪/src/Größe.cs", file.Path);
        Assert.AreEqual(3, file.LinesTotal);
        Assert.AreEqual(2, file.LinesHit);
        Assert.AreEqual(1, file.BranchesHit);
        Assert.AreEqual(2, file.BranchesTotal);
        Assert.IsEmpty(report.Warnings);
        Assert.IsTrue(stream.CanRead);
        Assert.IsFalse(stream.SynchronousReadAttempted);
    }

    [TestMethod]
    [DynamicData(nameof(ReadChunkSizes))]
    public async Task ParseMethods_FragmentedInput_KeepsMethodMeasurementsSeparate(int chunkSize)
    {
        await using var stream = new ScriptedReadStream(Document(), chunkSize);

        var report = CoberturaParser.ParseMethods(stream);

        var method = Assert.ContainsSingle(report.Methods);
        Assert.AreEqual("/répo/🧪/src/Größe.cs", method.File);
        Assert.AreEqual(2, method.LinesTotal);
        Assert.AreEqual(1, method.LinesHit);
        Assert.AreEqual(2, method.Complexity);
        Assert.IsTrue(stream.CanRead);
    }

    [TestMethod]
    [DynamicData(nameof(ReadChunkSizes))]
    public async Task ParseAsync_ReadFailsInsideClass_PropagatesIoFailureAndLeavesCallerStreamOpen(int chunkSize)
    {
        var bytes = Document();
        await using var stream = new ScriptedReadStream(bytes, chunkSize, asyncOnly: true);
        var failure = new IOException("source disconnected inside a class");
        stream.FailAt(bytes.AsSpan().IndexOf("<line "u8) + 4, failure);

        var thrown = await Assert.ThrowsExactlyAsync<IOException>(() => CoberturaParser.ParseAsync(stream, ct: testContext.CancellationToken));

        Assert.AreSame(failure, thrown);
        Assert.IsTrue(stream.CanRead);
    }

    [TestMethod]
    [DynamicData(nameof(ReadChunkSizes))]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task ParseAsync_CancelledAfterReadingStarts_DoesNotReturnPartialCoverage(int chunkSize)
    {
        var cancellationToken = testContext.CancellationToken;
        var bytes = Document();
        await using var stream = new ScriptedReadStream(bytes, chunkSize, asyncOnly: true);
        stream.PauseAt(bytes.AsSpan().IndexOf("<line "u8));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var parsing = CoberturaParser.ParseAsync(stream, ct: cancellation.Token);

        try
        {
            await stream.Paused.WaitAsync(cancellationToken);
            await cancellation.CancelAsync();
        }
        finally
        {
            stream.Resume();
            // Observe cancellation before disposing the caller-owned stream, even if waiting failed.
            var canceled = await Assert.ThrowsAsync<OperationCanceledException>(() => parsing);
            Assert.AreEqual(cancellation.Token, canceled.CancellationToken);
        }

        Assert.IsTrue(stream.CanRead);
        Assert.IsFalse(stream.SynchronousReadAttempted);
    }
}
