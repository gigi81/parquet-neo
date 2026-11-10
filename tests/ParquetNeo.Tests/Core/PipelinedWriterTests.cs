using ParquetNeo.Core;

namespace ParquetNeo.Tests.Core;

public class PipelinedWriterTests
{
    private static StagedBytes Bytes(params byte[] values)
    {
        var storage = new PooledBufferWriter<byte>();
        values.CopyTo(storage.GetSpan(values.Length));
        storage.Advance(values.Length);
        return new StagedBytes(storage);
    }

    [Test]
    public async Task TheWritesArriveInOrder_AndOneAtATime()
    {
        var destination = new TestDestination { Delay = TimeSpan.FromMilliseconds(5) };
        await using (var writer = new PipelinedWriter(destination, CancellationToken.None))
        {
            for (byte i = 0; i < 20; i++)
            {
                await writer.WriteAsync(Bytes(i, i));
            }

            await writer.CompleteAsync();
        }

        await Assert.That(destination.Bytes).IsEquivalentTo(Enumerable.Range(0, 20).SelectMany(i => new[] { (byte)i, (byte)i }).ToArray());
        await Assert.That(destination.MaxInFlight).IsEqualTo(1);
    }

    [Test]
    public async Task AWriteIsStarted_WithoutWaitingForItToEnd()
    {
        var destination = new TestDestination { HeldWrites = [0] };
        await using var writer = new PipelinedWriter(destination, CancellationToken.None);

        await writer.WriteAsync(Bytes(1));
        await destination.WaitForStartedAsync(1);

        // The call has returned while the write is still held: the caller is free to go on.
        await Assert.That(destination.InFlight).IsEqualTo(1);
        await Assert.That(destination.CompletedWrites).IsEqualTo(0);
        destination.Release(0);
        await writer.CompleteAsync();
        await Assert.That(destination.CompletedWrites).IsEqualTo(1);
    }

    [Test]
    public async Task TheNextWriteWaitsForTheOneBefore()
    {
        var destination = new TestDestination { HeldWrites = [0] };
        await using var writer = new PipelinedWriter(destination, CancellationToken.None);
        await writer.WriteAsync(Bytes(1));

        var second = writer.WriteAsync(Bytes(2)).AsTask();
        await Task.Delay(50);

        await Assert.That(second.IsCompleted).IsFalse();
        await Assert.That(destination.StartedWrites).IsEqualTo(1);
        destination.Release(0);
        await second;
        await writer.CompleteAsync();
        await Assert.That(destination.Bytes).IsEquivalentTo(new byte[] { 1, 2 });
    }

    [Test]
    public async Task AFailedWrite_IsReportedByTheNextCall_AndNothingMoreIsWritten()
    {
        var destination = new TestDestination { FailOnWrite = 0 };
        await using var writer = new PipelinedWriter(destination, CancellationToken.None);
        await writer.WriteAsync(Bytes(1));

        var exception = await Assert.ThrowsAsync<IOException>(async () => await writer.WriteAsync(Bytes(2)));

        await Assert.That(exception!.Message).Contains("write 0 failed");
        await Assert.That(destination.StartedWrites).IsEqualTo(1);
        await Assert.That(destination.InFlight).IsEqualTo(0);
    }

    [Test]
    public async Task AFailedWrite_IsReportedByComplete()
    {
        var destination = new TestDestination { FailOnWrite = 1 };
        await using var writer = new PipelinedWriter(destination, CancellationToken.None);
        await writer.WriteAsync(Bytes(1));
        await writer.WriteAsync(Bytes(2));

        await Assert.ThrowsAsync<IOException>(async () => await writer.CompleteAsync());
    }

    [Test]
    public async Task ACancelledToken_StopsBeforeTheNextWriteIsStarted()
    {
        using var cancellation = new CancellationTokenSource();
        var destination = new TestDestination();
        await using var writer = new PipelinedWriter(destination, cancellation.Token);
        await writer.WriteAsync(Bytes(1));
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await writer.WriteAsync(Bytes(2)));

        await Assert.That(destination.StartedWrites).IsEqualTo(1);
    }

    [Test]
    public async Task DisposingWaitsForTheWriteThatIsGoing_SoItsMemoryIsNotGivenBackTooSoon()
    {
        var destination = new TestDestination { HeldWrites = [0] };
        var writer = new PipelinedWriter(destination, CancellationToken.None);
        await writer.WriteAsync(Bytes(1, 2, 3));

        var disposing = writer.DisposeAsync().AsTask();
        await Task.Delay(50);
        await Assert.That(disposing.IsCompleted).IsFalse();
        destination.Release(0);
        await disposing;

        await Assert.That(destination.Bytes).IsEquivalentTo(new byte[] { 1, 2, 3 });
    }

    [Test]
    public async Task DisposingAfterAFailedWrite_DoesNotThrow()
    {
        var destination = new TestDestination { FailOnWrite = 0 };
        var writer = new PipelinedWriter(destination, CancellationToken.None);
        await writer.WriteAsync(Bytes(1));

        await Assert.That(async () => await writer.DisposeAsync()).ThrowsNothing();
    }
}
