using ParquetNeo.Linq;
using ParquetNeo.Serialization;
using ParquetNeo.Storage;

namespace ParquetNeo.Storage.EmulatorTests;

/// <summary>
/// The storages of S3 and of Azure Blob Storage against real servers (SeaweedFS, moto or another S3 server; Azurite): what the fakes of the unit tests only imitate, which is
/// how a server answers a request for a range, a listing in pages, a tag of version, an object that is not there.
/// </summary>
public class EmulatorTests
{
    public sealed class Reading
    {
        public int Id { get; set; }

        public string Name { get; set; } = "";

        public double Score { get; set; }
    }

    public static IEnumerable<string> Backends() => Backend.Names();

    private static byte[] RandomBytes(int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static Reading[] Readings(int firstId, int count) =>
        [.. Enumerable.Range(firstId, count).Select(i => new Reading { Id = i, Name = $"name-{i % 97}", Score = i / 8.0 })];

    private static byte[] ParquetFile(Reading[] rows, int rowGroupSize = 100)
    {
        var stream = new MemoryStream();
        ParquetSerializer.Serialize(rows, stream, new ParquetSerializerOptions { RowGroupSize = rowGroupSize });
        return stream.ToArray();
    }

    private static async Task<List<StorageObject>> List(IParquetStorage storage, string prefix)
    {
        var objects = new List<StorageObject>();
        await foreach (var item in storage.ListAsync(prefix))
        {
            objects.Add(item);
        }

        return objects;
    }

    private static async Task PutAll(Backend backend, IEnumerable<(string Key, byte[] Bytes)> objects)
    {
        await Parallel.ForEachAsync(objects, new ParallelOptions { MaxDegreeOfParallelism = 24 }, async (item, token) => await backend.PutAsync(item.Key, item.Bytes, token));
    }

    [Test]
    [MethodDataSource(nameof(Backends))]
    public async Task TheListing_GivesEveryObjectWithThePrefix_AcrossThePagesOfTheServer(string name)
    {
        var backend = await Backend.OpenAsync(name);
        string prefix = Backend.NewPrefix("list");
        string other = Backend.NewPrefix("other");
        // More than a page of S3 (1000 keys).
        var objects = Enumerable.Range(0, 1150).Select(i => ($"{prefix}part-{i:D4}.parquet", RandomBytes(10 + (i % 7), i))).ToArray();
        await PutAll(backend, [.. objects, ($"{other}x.parquet", RandomBytes(5, 1))]);

        var listed = await List(backend.CreateStorage(), prefix);

        await Assert.That(listed.Count).IsEqualTo(objects.Length);
        await Assert.That(listed.Select(o => o.Key).Order(StringComparer.Ordinal).SequenceEqual(objects.Select(o => o.Item1))).IsTrue();
        await Assert.That(listed.All(o => o.Length == objects.Single(x => x.Item1 == o.Key).Item2.Length)).IsTrue();
        await Assert.That((await List(backend.CreateStorage(), prefix + "nothing")).Count).IsEqualTo(0);
    }

    [Test]
    [MethodDataSource(nameof(Backends))]
    public async Task AnObject_IsReadByRanges_WhateverTheOffsetAndTheLength(string name)
    {
        var backend = await Backend.OpenAsync(name);
        string key = Backend.NewPrefix("ranges") + "data.bin";
        var data = RandomBytes((3 * 1024 * 1024) + 12_345, 42);
        await backend.PutAsync(key, data);
        var options = new RangeReadStreamOptions { BlockSize = 64 * 1024, CachedBlocks = 3, TailSize = 16 * 1024 };

        await using var stream = await backend.CreateStorage(options).OpenReadAsync(key);

        await Assert.That(stream.Length).IsEqualTo(data.Length);
        var random = new Random(5);
        var buffer = new byte[300_000];
        for (int i = 0; i < 150; i++)
        {
            int offset = random.Next(0, data.Length);
            int length = random.Next(1, buffer.Length);
            stream.Position = offset;
            int read = await stream.ReadAtLeastAsync(buffer.AsMemory(0, length), length, throwOnEndOfStream: false);

            await Assert.That(read).IsEqualTo(Math.Min(length, data.Length - offset));
            await Assert.That(buffer.AsSpan(0, read).SequenceEqual(data.AsSpan(offset, read))).IsTrue().Because($"offset {offset}, length {length}");
        }

        // The first and the last bytes, the end, and a read that starts at the end.
        stream.Position = 0;
        await Assert.That(await stream.ReadAsync(buffer.AsMemory(0, 1))).IsEqualTo(1);
        await Assert.That(buffer[0]).IsEqualTo(data[0]);
        stream.Position = data.Length - 1;
        await Assert.That(await stream.ReadAsync(buffer.AsMemory(0, 100))).IsEqualTo(1);
        await Assert.That(buffer[0]).IsEqualTo(data[^1]);
        await Assert.That(await stream.ReadAsync(buffer.AsMemory(0, 100))).IsEqualTo(0);

        // The whole object, in order.
        stream.Position = 0;
        var all = new MemoryStream();
        await stream.CopyToAsync(all);
        await Assert.That(all.ToArray().AsSpan().SequenceEqual(data)).IsTrue();
    }

    [Test]
    [MethodDataSource(nameof(Backends))]
    public async Task AnObjectThatIsNotThere_IsAFileNotFound(string name)
    {
        var backend = await Backend.OpenAsync(name);
        var storage = backend.CreateStorage();

        await Assert.That(async () => await storage.OpenReadAsync(Backend.NewPrefix("missing") + "nothing.parquet")).Throws<FileNotFoundException>();
    }

    [Test]
    [MethodDataSource(nameof(Backends))]
    public async Task AnObjectThatChangesWhileItIsRead_IsAnIOException_NotAFileOfTwoVersions(string name)
    {
        var backend = await Backend.OpenAsync(name);
        string key = Backend.NewPrefix("change") + "data.bin";
        await backend.PutAsync(key, RandomBytes(500_000, 1));
        await using var stream = await backend.CreateStorage(new RangeReadStreamOptions { BlockSize = 50_000, TailSize = 1000 }).OpenReadAsync(key);
        var buffer = new byte[100];
        await stream.ReadExactlyAsync(buffer);

        await backend.PutAsync(key, RandomBytes(500_000, 2)); // another version of the object, under the same key

        stream.Position = 300_000; // a block that was not read yet
        await Assert.That(async () => await stream.ReadExactlyAsync(buffer)).Throws<IOException>();
    }

    [Test]
    [MethodDataSource(nameof(Backends))]
    public async Task AParquetFile_IsDeserialized_ThroughTheStorage_ReadingLittleOfItWhenAFilterRulesOutRowGroups(string name)
    {
        var backend = await Backend.OpenAsync(name);
        string key = Backend.NewPrefix("file") + "readings.parquet";
        var rows = Readings(0, 100_000);
        var file = ParquetFile(rows, 10_000);
        await backend.PutAsync(key, file);
        var metered = new MeteredStorage(backend.CreateStorage(new RangeReadStreamOptions { BlockSize = 64 * 1024, TailSize = 64 * 1024 }));

        await using (var stream = await metered.OpenReadAsync(key))
        {
            var all = await ParquetSerializer.DeserializeAsync<Reading>(stream);
            await Assert.That(all.Count).IsEqualTo(rows.Length);
            await Assert.That(all.Select(r => r.Id).SequenceEqual(rows.Select(r => r.Id))).IsTrue();
            await Assert.That(all[54_321].Name).IsEqualTo(rows[54_321].Name);
        }

        long wholeFile = metered.Statistics.BytesRead;
        metered.Statistics.Reset();
        await using (var stream = await metered.OpenReadAsync(key))
        {
            var some = await ParquetSerializer.DeserializeAsync<Reading>(stream, new ParquetDeserializerOptions { RowGroupFilter = ParquetFilter.Column("Id").Between(25_000, 25_050) });

            await Assert.That(some.Any(r => r.Id == 25_025)).IsTrue();
            await Assert.That(some.Count).IsLessThan(rows.Length / 5);
        }

        await Assert.That(wholeFile).IsGreaterThanOrEqualTo(file.Length / 2);
        await Assert.That(file.Length).IsGreaterThan(500_000); // big enough for a row group to be a small part of it
        await Assert.That(metered.Statistics.BytesRead).IsLessThan(wholeFile / 4);
        await Assert.That(metered.Statistics.Requests).IsGreaterThan(0);
    }

    [Test]
    [MethodDataSource(nameof(Backends))]
    public async Task ADatasetOverAPrefix_IsQueried_SkippingTheFilesAndTheRowGroupsItCan(string name)
    {
        var backend = await Backend.OpenAsync(name);
        string prefix = Backend.NewPrefix("dataset");
        for (int file = 0; file < 4; file++)
        {
            await backend.PutAsync($"{prefix}readings-{file}.parquet", ParquetFile(Readings(file * 100_000, 20_000), rowGroupSize: 2_000));
        }

        await backend.PutAsync(prefix + "readme.txt", [1, 2, 3]);
        var expected = Enumerable.Range(0, 4).SelectMany(file => Readings(file * 100_000, 20_000)).ToArray();
        long sizeOfTheFiles = (await List(backend.CreateStorage(), prefix)).Where(o => o.Key.EndsWith(".parquet", StringComparison.Ordinal)).Sum(o => o.Length);
        var people = ParquetDataset.Open<Reading>(backend.CreateStorage(new RangeReadStreamOptions { BlockSize = 32 * 1024, TailSize = 32 * 1024 }), prefix, new ParquetDatasetOptions { MaxDegreeOfParallelism = 4 });

        var all = people.Select(r => r.Id).ToList();
        people.ReadStatistics.Reset();
        var some = people.Where(r => r.Id >= 200_000 && r.Id < 200_150).OrderBy(r => r.Id).Select(r => r.Id).ToList();
        long bytesOfTheQuery = people.ReadStatistics.BytesRead;
        var asynchronous = await people.Where(r => r.Id >= 100_000 && r.Id < 100_010).Select(r => r.Id).ToListAsync();

        await Assert.That(all.Order().SequenceEqual(expected.Select(r => r.Id))).IsTrue();
        await Assert.That(some.SequenceEqual(Enumerable.Range(200_000, 150))).IsTrue();
        await Assert.That(asynchronous.Order().SequenceEqual(Enumerable.Range(100_000, 10))).IsTrue();
        await Assert.That(await people.CountAsync()).IsEqualTo(80_000);
        await Assert.That(people.Count()).IsEqualTo(80_000);
        await Assert.That(await people.AnyAsync(r => r.Id == 319_999)).IsTrue();
        await Assert.That(await people.AnyAsync(r => r.Id == 320_000)).IsFalse();
        var plan = people.Where(r => r.Id >= 300_000).Explain();
        await Assert.That(plan.Files).IsEqualTo(4);
        await Assert.That(plan.FilesSkipped).IsEqualTo(3);
        await Assert.That(sizeOfTheFiles).IsGreaterThan(1_000_000);
        await Assert.That(bytesOfTheQuery).IsLessThan(sizeOfTheFiles / 4); // the footers of the four files and a row group of one
    }

    [Test]
    [MethodDataSource(nameof(Backends))]
    public async Task ACancelledToken_StopsTheReading(string name)
    {
        var backend = await Backend.OpenAsync(name);
        string key = Backend.NewPrefix("cancel") + "data.bin";
        await backend.PutAsync(key, RandomBytes(100_000, 3));
        using var cancel = new CancellationTokenSource();
        await using var stream = await backend.CreateStorage().OpenReadAsync(key);
        await cancel.CancelAsync();

        await Assert.That(async () => await stream.ReadAsync(new byte[100], cancel.Token)).Throws<OperationCanceledException>();
    }
}
