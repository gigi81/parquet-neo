using ParquetNeo.DataModel;
using ParquetNeo.Exceptions;
using ParquetNeo.Serialization;
using ParquetNeo.Tests.Core;

namespace ParquetNeo.Tests.Serialization;

public class DeserializerRobustnessTests
{
    private static ParquetDeserializerOptions Limited => new()
    {
        Reader = new ParquetReaderOptions { Codecs = TestParquetFile.Registry, MaxPageSize = 1 << 20, MaxRowsPerPage = 1 << 18, MaxFooterSize = 1 << 20 },
    };

    /// <summary>A seed from the name of a file that is the same in every run (<see cref="string.GetHashCode()"/> is not), so that a failure can be reproduced.</summary>
    private static int StableSeed(string name) => name.Aggregate(17, (seed, character) => unchecked((seed * 31) + character));

    private static bool IsExpected(Exception exception) => exception is ParquetException or NotSupportedException;

    [Test]
    public async Task AFooterThatCountsANegativeNumberOfRows_IsNotAnArgumentException()
    {
        // Found by damaging a file at random: the number of rows of a row group is taken from the file, and a list was sized with it.
        var bytes = File.ReadAllBytes(TestBytes.FixturePath("damaged-negative-row-count.parquet"));

        var sync = Record(() => ParquetSerializer.Deserialize<Document>(new MemoryStream(bytes), Limited));
        var lazy = Record(() => ParquetSerializer.DeserializeAll<Document>(new MemoryStream(bytes), Limited).ToList());
        var asynchronous = await RecordAsync(async () => await ParquetSerializer.DeserializeAsync<Document>(new MemoryStream(bytes), Limited));

        await Assert.That(sync is null || IsExpected(sync)).IsTrue().Because($"sync: {sync}");
        await Assert.That(lazy is null || IsExpected(lazy)).IsTrue().Because($"lazy: {lazy}");
        await Assert.That(asynchronous is null || IsExpected(asynchronous)).IsTrue().Because($"async: {asynchronous}");
    }

    [Test]
    public async Task RandomlyDamagedFiles_OnlyEverFailWithParquetExceptions_WhenReadIntoRows()
    {
        var files = new (string Name, byte[] Bytes)[]
        {
            ("our rows", File.ReadAllBytes(TestBytes.FixturePath("golden-serializer.parquet"))),
            ("pyarrow logical", File.ReadAllBytes(TestBytes.FixturePath("pyarrow-logical-optional-dictionary.parquet"))),
            ("pyarrow decimals", File.ReadAllBytes(TestBytes.FixturePath("pyarrow-decimal-optional-v2-snappy.parquet"))),
        };

        foreach (var (name, original) in files)
        {
            var random = new Random(StableSeed(name));
            for (int attempt = 0; attempt < 300; attempt++)
            {
                var damaged = (byte[])original.Clone();
                for (int k = random.Next(1, 4); k > 0; k--)
                {
                    damaged[random.Next(damaged.Length)] ^= (byte)(1 << random.Next(8));
                }

                var sync = Record(() => ParquetSerializer.Deserialize<Sample>(new MemoryStream(damaged), Limited));
                var asynchronous = await RecordAsync(async () => await ParquetSerializer.DeserializeAsync<Sample>(new MemoryStream(damaged), Limited));

                await Assert.That(sync is null || IsExpected(sync)).IsTrue().Because($"{name}, attempt {attempt}, sync: {sync}");
                await Assert.That(asynchronous is null || IsExpected(asynchronous)).IsTrue().Because($"{name}, attempt {attempt}, async: {asynchronous}");
            }
        }
    }

    [Test]
    public async Task RandomlyDamagedFiles_OnlyEverFailWithParquetExceptions_WhenReadIntoNestedRows()
    {
        var files = new (string Name, byte[] Bytes)[]
        {
            ("our nested rows", File.ReadAllBytes(TestBytes.FixturePath("golden-serializer-nested.parquet"))),
            ("pyarrow nested", File.ReadAllBytes(TestBytes.FixturePath("pyarrow-nested-v2-snappy.parquet"))),
            ("legacy lists", File.ReadAllBytes(TestBytes.FixturePath("golden-legacy-lists.parquet"))),
        };

        foreach (var (name, original) in files)
        {
            var random = new Random(StableSeed(name));
            for (int attempt = 0; attempt < 300; attempt++)
            {
                var damaged = (byte[])original.Clone();
                for (int k = random.Next(1, 4); k > 0; k--)
                {
                    damaged[random.Next(damaged.Length)] ^= (byte)(1 << random.Next(8));
                }

                var sync = Record(() => ParquetSerializer.Deserialize<Document>(new MemoryStream(damaged), Limited));
                var asynchronous = await RecordAsync(async () => await ParquetSerializer.DeserializeAsync<Document>(new MemoryStream(damaged), Limited));
                var legacy = Record(() => ParquetSerializer.Deserialize<LegacyShape>(new MemoryStream(damaged), Limited));

                await Assert.That(sync is null || IsExpected(sync)).IsTrue().Because($"{name}, attempt {attempt}, sync: {sync}");
                await Assert.That(asynchronous is null || IsExpected(asynchronous)).IsTrue().Because($"{name}, attempt {attempt}, async: {asynchronous}");
                await Assert.That(legacy is null || IsExpected(legacy)).IsTrue().Because($"{name}, attempt {attempt}, legacy: {legacy}");
            }
        }
    }

    private sealed class LegacyShape
    {
        [ParquetColumn("two_level")]
        public List<int>? TwoLevel { get; set; }

        [ParquetColumn("struct_element")]
        public List<LegacyItem>? StructElement { get; set; }

        [ParquetColumn("tuple")]
        public List<LegacyText>? Tuple { get; set; }

        [ParquetColumn("implicit")]
        public int[]? Implicit { get; set; }
    }

    private sealed class LegacyItem
    {
        [ParquetColumn("str")]
        public string? Text { get; set; }

        [ParquetColumn("num")]
        public int? Number { get; set; }
    }

    private sealed class LegacyText
    {
        [ParquetColumn("str")]
        public string? Text { get; set; }
    }

    [Test]
    public async Task TruncatedFiles_OnlyEverFailWithParquetExceptions_WhenReadIntoRows()
    {
        var bytes = File.ReadAllBytes(TestBytes.FixturePath("golden-serializer.parquet"));

        for (int cut = 0; cut < bytes.Length; cut += Math.Max(1, bytes.Length / 150))
        {
            var truncated = bytes[..cut];

            var sync = Record(() => ParquetSerializer.Deserialize<Sample>(new MemoryStream(truncated)));
            var asynchronous = await RecordAsync(async () => await ParquetSerializer.DeserializeAsync<Sample>(new MemoryStream(truncated)));

            await Assert.That(sync is ParquetException).IsTrue().Because($"cut at {cut}, sync: {sync}");
            await Assert.That(asynchronous is ParquetException).IsTrue().Because($"cut at {cut}, async: {asynchronous}");
        }
    }

    [Test]
    public async Task AFileThatIsNotParquet_IsReported_ByEveryPath()
    {
        var text = "this is a text file, certainly not Parquet at all"u8.ToArray();

        await Assert.That(() => ParquetSerializer.Deserialize<Sample>(new MemoryStream(text))).Throws<ParquetFormatException>();
        await Assert.That(async () => await ParquetSerializer.DeserializeAsync<Sample>(new MemoryStream(text))).Throws<ParquetFormatException>();
        await Assert.That(async () => await ParquetSerializer.DeserializeAsync<Sample>(new MemoryStream([]))).Throws<ParquetFormatException>();
    }

    // ---- the stream that serves the ranges read ahead ----

    [Test]
    public async Task APrefetchedStream_ServesTheRangesItHas_AndNothingElse()
    {
        var source = new MemoryStream(Enumerable.Range(0, 100).Select(i => (byte)i).ToArray());
        using var stream = new PrefetchedStream(source.Length);
        await stream.LoadKeptAsync(source, 90, 10, CancellationToken.None);
        await stream.LoadChunkAsync(source, 10, 20, CancellationToken.None);
        await stream.LoadChunkAsync(source, 30, 10, CancellationToken.None);

        var across = new byte[20];
        stream.Position = 20;
        stream.ReadExactly(across); // crosses from one range into the next
        var tail = new byte[10];
        stream.Seek(-10, SeekOrigin.End);
        stream.ReadExactly(tail);

        await Assert.That(across).IsEquivalentTo(Enumerable.Range(20, 20).Select(i => (byte)i));
        await Assert.That(tail).IsEquivalentTo(Enumerable.Range(90, 10).Select(i => (byte)i));
        await Assert.That(stream.Length).IsEqualTo(100L);
        await Assert.That(stream.CanSeek).IsTrue();
        await Assert.That(stream.CanWrite).IsFalse();
        stream.Position = 50;
        await Assert.That(() => stream.ReadByte()).Throws<InvalidOperationException>(); // never read ahead
        stream.DropLoaded();
        stream.Position = 20;
        await Assert.That(() => stream.ReadByte()).Throws<InvalidOperationException>(); // dropped
        stream.Position = 95;
        await Assert.That(stream.ReadByte()).IsEqualTo(95); // kept
        stream.Position = 100;
        await Assert.That(stream.ReadByte()).IsEqualTo(-1);
    }

    [Test]
    public async Task APrefetchedStream_CannotBeWritten()
    {
        using var stream = new PrefetchedStream(10);

        await Assert.That(() => stream.Write([1], 0, 1)).Throws<NotSupportedException>();
        await Assert.That(() => stream.SetLength(5)).Throws<NotSupportedException>();
    }

    private static Exception? Record(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task<Exception?> RecordAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }
}
