using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;
[TestClass]
public sealed class ParserStreamScenarioTests(TestContext testContext)
{
    private static byte[] Document() => Cobertura.NewDoc().WithSource("/répo/🧪").AddClass("src/Größe.cs", "Example", c => c.Method("M", "()", "2", m => m.Line(1, 3).Line(2, 0)).Line(1, 3).Line(2, 0).Branch(3, "50% (1/2)")).ToBytes();
    [TestMethod]
    [StreamScenarioSource]
    public async Task ParseAsync_FragmentedNonSeekableInput_PreservesUnicodeAndMeasurements(StreamScenario scenario)
    {
        await using var stream = scenario.Open(Document(), asyncOnly: true);
        var report = await CoberturaParser.ParseAsync(stream);
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
    [StreamScenarioSource]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task ParseAsync_ReadPausesAndResumes_ReturnsCompleteCoverage(StreamScenario scenario)
    {
        var cancellationToken = testContext.CancellationToken;
        var bytes = Document();
        await using var stream = scenario.Open(bytes, asyncOnly: true);
        stream.PauseAt(bytes.AsSpan().IndexOf("<line "u8) + 4);
        var parsing = CoberturaParser.ParseAsync(stream, ct: cancellationToken);
        bool wasPending;
        CoverageReport report;
        try
        {
            await stream.Paused.WaitAsync(cancellationToken);
            wasPending = !parsing.IsCompleted;
        }
        finally
        {
            stream.Resume();
            report = await parsing;
        }

        Assert.IsTrue(wasPending);
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
    [StreamScenarioSource]
    public async Task ParseMethods_FragmentedInput_KeepsMethodMeasurementsSeparate(StreamScenario scenario)
    {
        await using var stream = scenario.Open(Document());
        var report = CoberturaParser.ParseMethods(stream);
        var method = Assert.ContainsSingle(report.Methods);
        Assert.AreEqual("/répo/🧪/src/Größe.cs", method.File);
        Assert.AreEqual(2, method.LinesTotal);
        Assert.AreEqual(1, method.LinesHit);
        Assert.AreEqual(2, method.Complexity);
        Assert.IsTrue(stream.CanRead);
    }

    [TestMethod]
    [StreamScenarioSource]
    public async Task ParseAsync_ReadFailsInsideClass_PropagatesIoFailureAndLeavesCallerStreamOpen(StreamScenario scenario)
    {
        var bytes = Document();
        await using var stream = scenario.Open(bytes, asyncOnly: true);
        var failure = new IOException("source disconnected inside a class");
        stream.FailAt(bytes.AsSpan().IndexOf("<line "u8) + 4, failure);
        var parsing = CoberturaParser.ParseAsync(stream);
        var thrown = await Assert.ThrowsExactlyAsync<IOException>(() => parsing);
        Assert.AreSame(failure, thrown);
        Assert.IsTrue(stream.CanRead);
    }

    [TestMethod]
    [StreamScenarioSource]
    [Timeout(10_000, CooperativeCancellation = true)]
    public async Task ParseAsync_CancelledAfterReadingStarts_DoesNotReturnPartialCoverage(StreamScenario scenario)
    {
        var cancellationToken = testContext.CancellationToken;
        var bytes = Document();
        await using var stream = scenario.Open(bytes, asyncOnly: true);
        stream.PauseAt(bytes.AsSpan().IndexOf("<line "u8));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var parsing = CoberturaParser.ParseAsync(stream, ct: cancellation.Token);
        var canceled = false;
        try
        {
            await stream.Paused.WaitAsync(cancellationToken);
            await cancellation.CancelAsync();
        }
        finally
        {
            stream.Resume();
            // Observe the operation before the caller-owned stream leaves its scope.
            try
            {
                await parsing;
            }
            catch (OperationCanceledException ex)when (ex.CancellationToken == cancellation.Token)
            {
                canceled = true;
            }
        }

        Assert.IsTrue(canceled);
        Assert.IsTrue(stream.CanRead);
        Assert.IsFalse(stream.SynchronousReadAttempted);
    }
}
