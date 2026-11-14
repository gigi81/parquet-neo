using ParquetNeo.Storage;
using ParquetNeo.Tests.Storage;

namespace ParquetNeo.Tests.Storage;

/// <summary>A stream over an object of a storage: the bytes it gives, how seeking works, and how few requests it makes.</summary>
public class RangeReadStreamTests
{
    private static byte[] Bytes(int length) => Enumerable.Range(0, length).Select(i => (byte)((i * 31) + (i / 251))).ToArray();

    private static async Task<(InMemoryStorage Storage, Stream Stream, byte[] Data)> Open(int length, int blockSize = 1000, int cachedBlocks = 4)
    {
        var data = Bytes(length);
        var storage = new InMemoryStorage { Options = new RangeReadStreamOptions { BlockSize = blockSize, CachedBlocks = cachedBlocks, TailSize = 0 } }.Put("a", data);
        return (storage, await storage.OpenReadAsync("a"), data);
    }

    [Test]
    public async Task ItHasTheLengthOfTheObject_AndStartsAtTheBeginning()
    {
        var (_, stream, _) = await Open(2500);

        await Assert.That(stream.Length).IsEqualTo(2500);
        await Assert.That(stream.Position).IsEqualTo(0);
        await Assert.That(stream.CanRead).IsTrue();
        await Assert.That(stream.CanSeek).IsTrue();
        await Assert.That(stream.CanWrite).IsFalse();
    }

    [Test]
    public async Task ReadingItAll_GivesTheBytes_ForAnySizeOfRead()
    {
        foreach (int read in new[] { 1, 7, 999, 1000, 1001, 4096, 100_000 })
        {
            var (_, stream, data) = await Open(5321);
            var all = new MemoryStream();
            var buffer = new byte[read];
            int count;
            while ((count = await stream.ReadAsync(buffer)) > 0)
            {
                all.Write(buffer, 0, count);
            }

            await Assert.That(all.ToArray().AsSpan().SequenceEqual(data)).IsTrue();
        }
    }

    [Test]
    public async Task ManySmallReadsInABlock_AreOneRequest()
    {
        var (storage, stream, _) = await Open(5000, blockSize: 1000);

        var buffer = new byte[10];
        for (int i = 0; i < 100; i++)
        {
            await stream.ReadExactlyAsync(buffer);
        }

        await Assert.That(storage.Requests.Count).IsEqualTo(1);
        await Assert.That(storage.Requests[0]).IsEqualTo(new StorageRequest("a", 0, 1000));
    }

    [Test]
    public async Task ALargeRead_IsOneRequestForExactlyWhatItAsks()
    {
        var (storage, stream, data) = await Open(50_000, blockSize: 1000);
        stream.Position = 12_345;

        var buffer = new byte[20_000];
        await stream.ReadExactlyAsync(buffer);

        await Assert.That(storage.Requests).IsEquivalentTo(new[] { new StorageRequest("a", 12_345, 20_000) });
        await Assert.That(buffer.AsSpan().SequenceEqual(data.AsSpan(12_345, 20_000))).IsTrue();
    }

    [Test]
    public async Task TheLastBlock_IsAsShortAsTheObject()
    {
        var (storage, stream, data) = await Open(2500, blockSize: 1000);
        stream.Position = 2400;

        var buffer = new byte[100];
        await stream.ReadExactlyAsync(buffer);

        await Assert.That(storage.Requests).IsEquivalentTo(new[] { new StorageRequest("a", 2000, 500) });
        await Assert.That(buffer.AsSpan().SequenceEqual(data.AsSpan(2400, 100))).IsTrue();
        await Assert.That(await stream.ReadAsync(new byte[10])).IsEqualTo(0);
    }

    [Test]
    public async Task ABlockThatWasRead_IsKept_AndTheOldestIsLetGo()
    {
        var (storage, stream, _) = await Open(10_000, blockSize: 1000, cachedBlocks: 2);
        var buffer = new byte[4];

        async Task ReadAt(long position)
        {
            stream.Position = position;
            await stream.ReadExactlyAsync(buffer);
        }

        await ReadAt(10); // block 0
        await ReadAt(1010); // block 1
        await ReadAt(20); // block 0 again: kept
        await ReadAt(2010); // block 2: block 1, the one read longest ago, is let go
        await ReadAt(30); // block 0: still kept
        await ReadAt(1020); // block 1: requested again

        await Assert.That(storage.Requests.Select(r => r.Offset)).IsEquivalentTo(new long[] { 0, 1000, 2000, 1000 });
    }

    [Test]
    public async Task ARead_ThatCrossesABlock_GivesWhatIsInThisBlock_AndTheRestIsReadNext()
    {
        var (_, stream, data) = await Open(3000, blockSize: 1000);
        stream.Position = 990;

        var buffer = new byte[50];
        int first = await stream.ReadAsync(buffer);
        int second = await stream.ReadAsync(buffer.AsMemory(first));

        await Assert.That(first).IsEqualTo(10);
        await Assert.That(second).IsEqualTo(40);
        await Assert.That(buffer.AsSpan().SequenceEqual(data.AsSpan(990, 50))).IsTrue();
    }

    [Test]
    public async Task AStorageThatGivesLessThanWasAskedFor_IsAskedAgain()
    {
        var (storage, stream, data) = await Open(20_000, blockSize: 1000);
        storage.MaxBytesPerRead = 333;

        var small = new byte[500];
        stream.Position = 100;
        await stream.ReadExactlyAsync(small);
        var large = new byte[5000];
        stream.Position = 7000;
        await stream.ReadExactlyAsync(large);

        await Assert.That(small.AsSpan().SequenceEqual(data.AsSpan(100, 500))).IsTrue();
        await Assert.That(large.AsSpan().SequenceEqual(data.AsSpan(7000, 5000))).IsTrue();
        await Assert.That(storage.Requests.All(r => r.Count <= 333)).IsTrue();
    }

    [Test]
    public async Task SeekingAndPosition_FollowTheRulesOfAStream()
    {
        var (_, stream, _) = await Open(2500);

        await Assert.That(stream.Seek(100, SeekOrigin.Begin)).IsEqualTo(100);
        await Assert.That(stream.Seek(50, SeekOrigin.Current)).IsEqualTo(150);
        await Assert.That(stream.Seek(-10, SeekOrigin.End)).IsEqualTo(2490);
        stream.Position = 2500;
        await Assert.That(await stream.ReadAsync(new byte[10])).IsEqualTo(0);
        stream.Position = 9999;
        await Assert.That(await stream.ReadAsync(new byte[10])).IsEqualTo(0);
        await Assert.That(() => stream.Seek(-1, SeekOrigin.Begin)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => stream.Position = -5).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task TheSynchronousRead_GivesTheSameBytes()
    {
        var (_, stream, data) = await Open(5000, blockSize: 1000);
        stream.Position = 1234;

        var buffer = new byte[300];
        stream.ReadExactly(buffer);
        var large = new byte[3000];
        stream.ReadExactly(large);

        await Assert.That(buffer.AsSpan().SequenceEqual(data.AsSpan(1234, 300))).IsTrue();
        await Assert.That(large.AsSpan().SequenceEqual(data.AsSpan(1534, 3000))).IsTrue();
    }

    [Test]
    public async Task TheFirstReadOfTheEnd_TakesTheTailInOneRequest_AndTheFooterIsInIt()
    {
        var data = Bytes(100_000);
        var storage = new InMemoryStorage { Options = new RangeReadStreamOptions { BlockSize = 1000, TailSize = 8000 } }.Put("a", data);
        await using var stream = await storage.OpenReadAsync("a");

        // As a Parquet reader does: the last 8 bytes, then the footer in front of them, then a piece of the footer again.
        var last = new byte[8];
        stream.Position = 99_992;
        await stream.ReadExactlyAsync(last);
        var footer = new byte[5000];
        stream.Position = 99_992 - 5000;
        await stream.ReadExactlyAsync(footer);
        var part = new byte[300];
        stream.Position = 97_000;
        await stream.ReadExactlyAsync(part);

        await Assert.That(storage.Requests).IsEquivalentTo(new[] { new StorageRequest("a", 92_000, 8000) });
        await Assert.That(last.AsSpan().SequenceEqual(data.AsSpan(99_992, 8))).IsTrue();
        await Assert.That(footer.AsSpan().SequenceEqual(data.AsSpan(99_992 - 5000, 5000))).IsTrue();
        await Assert.That(part.AsSpan().SequenceEqual(data.AsSpan(97_000, 300))).IsTrue();
    }

    [Test]
    public async Task AnObjectShorterThanTheTail_IsReadWholeAtTheFirstReadOfItsEnd()
    {
        var data = Bytes(5000);
        var storage = new InMemoryStorage { Options = new RangeReadStreamOptions { BlockSize = 1000, TailSize = 64 * 1024 } }.Put("a", data);
        await using var stream = await storage.OpenReadAsync("a");

        stream.Position = 4990;
        await stream.ReadExactlyAsync(new byte[10]);
        stream.Position = 0;
        var start = new byte[100];
        await stream.ReadExactlyAsync(start);

        // The whole object came with the first read of its end; the start of it is in it.
        await Assert.That(storage.Requests).IsEquivalentTo(new[] { new StorageRequest("a", 0, 5000) });
        await Assert.That(start.AsSpan().SequenceEqual(data.AsSpan(0, 100))).IsTrue();
    }

    [Test]
    public async Task ReadsBeforeTheTail_AreNotTheTail()
    {
        var storage = new InMemoryStorage { Options = new RangeReadStreamOptions { BlockSize = 1000, TailSize = 2000 } }.Put("a", Bytes(10_000));
        await using var stream = await storage.OpenReadAsync("a");

        await stream.ReadExactlyAsync(new byte[4]);
        stream.Position = 7999;
        await stream.ReadExactlyAsync(new byte[1]);

        await Assert.That(storage.Requests.Select(r => (r.Offset, r.Count))).IsEquivalentTo(new[] { (0L, 1000L), (7000L, 1000L) });
    }

    [Test]
    public async Task ItIsReadOnly()
    {
        var (_, stream, _) = await Open(100);

        await Assert.That(() => stream.Write(new byte[1], 0, 1)).Throws<NotSupportedException>();
        await Assert.That(() => stream.SetLength(5)).Throws<NotSupportedException>();
    }

    [Test]
    public async Task AnObjectThatIsShorterThanItSaid_IsAnError()
    {
        var storage = new InMemoryStorage().Put("a", Bytes(500));
        await using var stream = new ShortStream(storage);

        await Assert.That(async () => await stream.ReadAsync(new byte[10])).Throws<IOException>();
    }

    [Test]
    public async Task TheOptionsMustMakeSense()
    {
        await Assert.That(() => new ShortStream(new InMemoryStorage(), new RangeReadStreamOptions { BlockSize = 0 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new ShortStream(new InMemoryStorage(), new RangeReadStreamOptions { CachedBlocks = 0 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new ShortStream(new InMemoryStorage(), new RangeReadStreamOptions { TailSize = -1 })).Throws<ArgumentOutOfRangeException>();
    }

    /// <summary>Says it is 1000 bytes, and gives nothing.</summary>
    private sealed class ShortStream(InMemoryStorage storage, RangeReadStreamOptions? options = null) : RangeReadStream(1000, options)
    {
        protected override ValueTask<int> ReadRangeAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
        {
            storage.Requests.Add(new StorageRequest("short", offset, destination.Length));
            return ValueTask.FromResult(0);
        }
    }
}
