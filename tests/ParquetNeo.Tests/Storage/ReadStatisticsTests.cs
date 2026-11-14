using ParquetNeo.Storage;

namespace ParquetNeo.Tests.Storage;

/// <summary>Counting what is read from a storage: objects, listings, requests and bytes.</summary>
public class ReadStatisticsTests
{
    private static InMemoryStorage Storage() =>
        new InMemoryStorage { Options = new RangeReadStreamOptions { BlockSize = 1000, TailSize = 0 } }
            .Put("a", Enumerable.Range(0, 10_000).Select(i => (byte)i).ToArray())
            .Put("b", Enumerable.Range(0, 3_000).Select(i => (byte)i).ToArray());

    [Test]
    public async Task TheRequestsAndTheBytes_AreThoseThatWereMadeToTheStorage()
    {
        var inner = Storage();
        var metered = new MeteredStorage(inner);

        await using (var stream = await metered.OpenReadAsync("a"))
        {
            await stream.ReadExactlyAsync(new byte[10]);
            stream.Position = 5000;
            await stream.ReadExactlyAsync(new byte[3000]);
        }

        await Assert.That(metered.Statistics.ObjectsOpened).IsEqualTo(1);
        await Assert.That(metered.Statistics.Requests).IsEqualTo(inner.Requests.Count);
        await Assert.That(metered.Statistics.BytesRead).IsEqualTo(inner.BytesRead);
        await Assert.That(metered.Statistics.Requests).IsEqualTo(2);
        await Assert.That(metered.Statistics.BytesRead).IsEqualTo(1000 + 3000);
    }

    [Test]
    public async Task AReadThatABlockThatIsKeptAnswers_IsNotARequest()
    {
        var metered = new MeteredStorage(Storage());

        await using var stream = await metered.OpenReadAsync("a");
        for (int i = 0; i < 50; i++)
        {
            stream.Position = i * 10;
            await stream.ReadExactlyAsync(new byte[10]);
        }

        await Assert.That(metered.Statistics.Requests).IsEqualTo(1);
        await Assert.That(metered.Statistics.BytesRead).IsEqualTo(1000);
    }

    [Test]
    public async Task ListingsAndObjects_AreCounted_AndSeveralObjectsAddUp()
    {
        var inner = Storage();
        var metered = new MeteredStorage(inner);

        await foreach (var item in metered.ListAsync(""))
        {
            await using var stream = await metered.OpenReadAsync(item.Key);
            await stream.ReadExactlyAsync(new byte[5]);
        }

        await Assert.That(metered.Statistics.Listings).IsEqualTo(1);
        await Assert.That(metered.Statistics.ObjectsOpened).IsEqualTo(2);
        await Assert.That(metered.Statistics.Requests).IsEqualTo(2);
        await Assert.That(metered.Statistics.BytesRead).IsEqualTo(inner.BytesRead);
    }

    [Test]
    public async Task ResetStartsAgain_AndASnapshotDoesNotChange()
    {
        var metered = new MeteredStorage(Storage());
        await using var stream = await metered.OpenReadAsync("b");
        await stream.ReadExactlyAsync(new byte[5]);

        var before = metered.Statistics.Snapshot();
        metered.Statistics.Reset();
        stream.Position = 2500;
        await stream.ReadExactlyAsync(new byte[5]);

        await Assert.That(before).IsEqualTo(new ReadStatisticsSnapshot(1, 0, 1, 1000));
        await Assert.That(metered.Statistics.Snapshot()).IsEqualTo(new ReadStatisticsSnapshot(0, 0, 1, 1000));
        await Assert.That(before.ToString()).Contains("1,000 bytes in 1 requests");
        await Assert.That(metered.Statistics.ToString()).Contains("1,000 bytes");
    }

    [Test]
    public async Task ACounterThatIsGiven_IsSharedByTheStorages()
    {
        var counter = new ReadStatistics();
        var first = new MeteredStorage(Storage(), counter);
        var second = new MeteredStorage(Storage(), counter);

        await using (var stream = await first.OpenReadAsync("b"))
        {
            await stream.ReadExactlyAsync(new byte[5]);
        }

        await using (var stream = await second.OpenReadAsync("b"))
        {
            await stream.ReadExactlyAsync(new byte[5]);
        }

        await Assert.That(first.Statistics).IsSameReferenceAs(counter);
        await Assert.That(counter.ObjectsOpened).IsEqualTo(2);
        await Assert.That(counter.Requests).IsEqualTo(2);
    }

    [Test]
    public async Task ReadsFromSeveralThreads_AllAddUp()
    {
        var inner = Storage();
        var metered = new MeteredStorage(inner);

        await Task.WhenAll(Enumerable.Range(0, 16).Select(async i =>
        {
            await using var stream = await metered.OpenReadAsync("a");
            stream.Position = (i % 10) * 1000;
            await stream.ReadExactlyAsync(new byte[100]);
        }));

        await Assert.That(metered.Statistics.ObjectsOpened).IsEqualTo(16);
        await Assert.That(metered.Statistics.Requests).IsEqualTo(16);
        await Assert.That(metered.Statistics.BytesRead).IsEqualTo(16 * 1000);
    }

    [Test]
    public async Task AFileOfADisk_CountsTheBytesThatWereReadAndTheReads()
    {
        string root = Path.Combine(Path.GetTempPath(), "parquet-neo-metered-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "f.bin"), new byte[5000]);
            var metered = new MeteredStorage(new LocalFileStorage(root));

            await using (var stream = await metered.OpenReadAsync("f.bin"))
            {
                await Assert.That(stream.Length).IsEqualTo(5000);
                await stream.ReadExactlyAsync(new byte[100]);
                stream.Position = 4000;
                var rest = new byte[2000];
                int read = await stream.ReadAsync(rest);
                await Assert.That(read).IsEqualTo(1000);
                await Assert.That(stream.CanSeek).IsTrue();
                await Assert.That(stream.CanWrite).IsFalse();
            }

            await Assert.That(metered.Statistics.ObjectsOpened).IsEqualTo(1);
            await Assert.That(metered.Statistics.BytesRead).IsEqualTo(1100);
            await Assert.That(metered.Statistics.Requests).IsEqualTo(2);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Test]
    public async Task ThePieces_AreChecked()
    {
        await Assert.That(() => new MeteredStorage(null!)).Throws<ArgumentNullException>();
    }
}
