using ParquetNeo.Serialization;
using ParquetNeo.Tests.Core;

namespace ParquetNeo.Tests.Serialization;

/// <summary>The asynchronous serializer encodes a row group while the one before is written; the file must be the same, and the writes orderly.</summary>
public class AsyncWriteTests
{
    private const int RowGroupSize = 1_000;

    private sealed class Row
    {
        public long Id { get; set; }

        public string? Name { get; set; }

        public double? Score { get; set; }

        public List<int>? Tags { get; set; }
    }

    private static Row[] Rows(int count) =>
        Enumerable.Range(0, count).Select(i => new Row
        {
            Id = i,
            Name = i % 5 == 0 ? null : "name-" + (i % 60),
            Score = i % 3 == 0 ? null : i * 0.25,
            Tags = i % 4 == 0 ? null : Enumerable.Range(0, i % 4).ToList(),
        }).ToArray();

    private static readonly ParquetSerializerOptions Options = new() { RowGroupSize = RowGroupSize };

    private static byte[] Sync(IEnumerable<Row> rows)
    {
        var stream = new MemoryStream();
        ParquetSerializer.Serialize(rows, stream, Options);
        return stream.ToArray();
    }

    private static async IAsyncEnumerable<Row> Async(IEnumerable<Row> rows)
    {
        foreach (var row in rows)
        {
            await Task.Yield();
            yield return row;
        }
    }

    [Test]
    [Arguments(1)]
    [Arguments(999)]
    [Arguments(1_000)]
    [Arguments(3_500)]
    [Arguments(7_000)]
    public async Task TheFile_IsTheSameAsTheSynchronousOne_WhateverHowManyRowGroups(int count)
    {
        var rows = Rows(count);
        var destination = new TestDestination { Delay = TimeSpan.FromMilliseconds(1) };

        await ParquetSerializer.SerializeAsync(rows, destination, Options);

        await Assert.That(destination.Bytes.AsSpan().SequenceEqual(Sync(rows))).IsTrue();
        await Assert.That(destination.MaxInFlight).IsEqualTo(1);
    }

    [Test]
    public async Task TheFileOfRowsThatArriveAsynchronously_IsTheSameToo()
    {
        var rows = Rows(4_200);
        var destination = new TestDestination { Delay = TimeSpan.FromMilliseconds(1) };

        await ParquetSerializer.SerializeAsync(Async(rows), destination, Options);

        await Assert.That(destination.Bytes.AsSpan().SequenceEqual(Sync(rows))).IsTrue();
    }

    [Test]
    public async Task ARowGroupIsEncodedWhileTheOneBeforeIsBeingWritten()
    {
        var rows = Rows(3_000);
        var destination = new TestDestination { HeldWrites = [1] };
        int inFlightWhileCollecting = -1;
        int completedWhileCollecting = -1;
        IEnumerable<Row> Watching()
        {
            for (int i = 0; i < rows.Length; i++)
            {
                if (i == RowGroupSize + 5)
                {
                    // The first row group has been encoded and its write started (and it is held); the serializer must be asking for
                    // the rows of the second group, and not be waiting for the write.
                    inFlightWhileCollecting = destination.InFlight;
                    completedWhileCollecting = destination.CompletedWrites;
                    destination.Release(1);
                }

                yield return rows[i];
            }
        }

        await ParquetSerializer.SerializeAsync(Watching(), destination, Options).WaitAsync(TimeSpan.FromSeconds(30));

        await Assert.That(inFlightWhileCollecting).IsEqualTo(1);
        await Assert.That(completedWhileCollecting).IsEqualTo(1);
        await Assert.That(destination.Bytes.AsSpan().SequenceEqual(Sync(rows))).IsTrue();
    }

    [Test]
    public async Task ACancellationWhileAWriteIsGoing_WaitsForTheWrite_AndThrows()
    {
        using var cancellation = new CancellationTokenSource();
        var rows = Rows(3_000);
        var destination = new TestDestination { HeldWrites = [1] };
        IEnumerable<Row> Cancelling()
        {
            for (int i = 0; i < rows.Length; i++)
            {
                if (i == RowGroupSize + 5)
                {
                    cancellation.Cancel();
                }

                yield return rows[i];
            }
        }

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await ParquetSerializer.SerializeAsync(Cancelling(), destination, Options, cancellation.Token));

        // Nothing is left writing: the memory of the write that was held is the stream's no more.
        await Assert.That(destination.InFlight).IsEqualTo(0);
    }

    [Test]
    public async Task AWriteThatFails_ThrowsItsException_AndNothingIsLeftWriting()
    {
        var destination = new TestDestination { FailOnWrite = 1 };

        var exception = await Assert.ThrowsAsync<IOException>(async () => await ParquetSerializer.SerializeAsync(Rows(5_000), destination, Options));

        await Assert.That(exception!.Message).Contains("write 1 failed");
        await Assert.That(destination.InFlight).IsEqualTo(0);
    }

    [Test]
    public async Task ARowThatIsNull_ThrowsWithAWriteGoing_AndNothingIsLeftWriting()
    {
        var rows = Rows(2_500).Cast<Row?>().ToList();
        rows[RowGroupSize + 10] = null;
        var destination = new TestDestination { HeldWrites = [1] };
        IEnumerable<Row> Releasing()
        {
            foreach (var row in rows)
            {
                if (row is null)
                {
                    destination.Release(1);
                }

                yield return row!;
            }
        }

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await ParquetSerializer.SerializeAsync(Releasing(), destination, Options));

        await Assert.That(destination.InFlight).IsEqualTo(0);
    }

    [Test]
    public async Task AnEmptyCollection_WritesAFileWithNoRows()
    {
        var destination = new TestDestination();

        await ParquetSerializer.SerializeAsync(Array.Empty<Row>(), destination, Options);

        await Assert.That(destination.Bytes.AsSpan().SequenceEqual(Sync([]))).IsTrue();
        await Assert.That(ParquetSerializer.Deserialize<Row>(new MemoryStream(destination.Bytes)).Count).IsEqualTo(0);
    }
}
