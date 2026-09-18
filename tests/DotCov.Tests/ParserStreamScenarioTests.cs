using DotCov.Tests.Infrastructure;

namespace DotCov.Tests;

public sealed class ParserStreamScenarioTests
{
    private static byte[] Document() => Cobertura.NewDoc()
        .WithSource("/répo/🧪")
        .AddClass("src/Größe.cs", "Example", c => c
            .Method("M", "()", "2", m => m.Line(1, 3).Line(2, 0))
            .Line(1, 3).Line(2, 0).Branch(3, "50% (1/2)"))
        .ToBytes();

    [Test]
    [StreamScenarioSource]
    public async Task ParseAsync_FragmentedNonSeekableInput_PreservesUnicodeAndMeasurements(StreamScenario scenario)
    {
        using var stream = scenario.Open(Document(), asyncOnly: true);

        var report = await CoberturaParser.ParseAsync(stream);

        var file = await Assert.That(report.Files).HasSingleItem();
        await Assert.That(file.Path).IsEqualTo("/répo/🧪/src/Größe.cs");
        await Assert.That(file.LinesTotal).IsEqualTo(3);
        await Assert.That(file.LinesHit).IsEqualTo(2);
        await Assert.That(file.BranchesHit).IsEqualTo(1);
        await Assert.That(file.BranchesTotal).IsEqualTo(2);
        await Assert.That(report.Warnings).IsEmpty();
        await Assert.That(stream.CanRead).IsTrue();
    }

    [Test]
    [StreamScenarioSource]
    [Timeout(10_000)]
    public async Task ParseAsync_ReadPausesAndResumes_ReturnsCompleteCoverage(
        StreamScenario scenario, CancellationToken cancellationToken)
    {
        var bytes = Document();
        using var stream = scenario.Open(bytes, asyncOnly: true);
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

        await Assert.That(wasPending).IsTrue();
        var file = await Assert.That(report.Files).HasSingleItem();
        await Assert.That(file.Path).IsEqualTo("/répo/🧪/src/Größe.cs");
        await Assert.That(file.LinesTotal).IsEqualTo(3);
        await Assert.That(file.LinesHit).IsEqualTo(2);
        await Assert.That(file.BranchesHit).IsEqualTo(1);
        await Assert.That(file.BranchesTotal).IsEqualTo(2);
        await Assert.That(report.Warnings).IsEmpty();
        await Assert.That(stream.CanRead).IsTrue();
        await Assert.That(stream.SynchronousReadAttempted).IsFalse();
    }

    [Test]
    [StreamScenarioSource]
    public async Task ParseMethods_FragmentedInput_KeepsMethodMeasurementsSeparate(StreamScenario scenario)
    {
        using var stream = scenario.Open(Document());

        var report = CoberturaParser.ParseMethods(stream);

        var method = await Assert.That(report.Methods).HasSingleItem();
        await Assert.That(method.File).IsEqualTo("/répo/🧪/src/Größe.cs");
        await Assert.That(method.LinesTotal).IsEqualTo(2);
        await Assert.That(method.LinesHit).IsEqualTo(1);
        await Assert.That(method.Complexity).IsEqualTo(2);
        await Assert.That(stream.CanRead).IsTrue();
    }

    [Test]
    [StreamScenarioSource]
    public async Task ParseAsync_ReadFailsInsideClass_PropagatesIoFailureAndLeavesCallerStreamOpen(StreamScenario scenario)
    {
        var bytes = Document();
        using var stream = scenario.Open(bytes, asyncOnly: true);
        var failure = new IOException("source disconnected inside a class");
        stream.FailAt(bytes.AsSpan().IndexOf("<line "u8) + 4, failure);

        var parsing = CoberturaParser.ParseAsync(stream);
        var thrown = await Assert.ThrowsExactlyAsync<IOException>(() => parsing);

        await Assert.That(thrown).IsSameReferenceAs(failure);
        await Assert.That(stream.CanRead).IsTrue();
    }

    [Test]
    [StreamScenarioSource]
    [Timeout(10_000)]
    public async Task ParseAsync_CancelledAfterReadingStarts_DoesNotReturnPartialCoverage(
        StreamScenario scenario, CancellationToken cancellationToken)
    {
        var bytes = Document();
        using var stream = scenario.Open(bytes, asyncOnly: true);
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
            try { await parsing; }
            catch (OperationCanceledException ex) when (ex.CancellationToken == cancellation.Token) { canceled = true; }
        }

        await Assert.That(canceled).IsTrue();
        await Assert.That(stream.CanRead).IsTrue();
        await Assert.That(stream.SynchronousReadAttempted).IsFalse();
    }
}
