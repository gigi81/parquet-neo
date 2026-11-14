using ParquetNeo.Linq;
using ParquetNeo.Serialization;
using ParquetNeo.Storage;
using ParquetNeo.Tests.Storage;

namespace ParquetNeo.Storage.S3.Tests;

/// <summary>The storage of a bucket of Amazon S3, against a bucket in memory that answers as S3 does.</summary>
public class S3ParquetStorageTests
{
    public sealed class Reading
    {
        public int Id { get; set; }

        public string Name { get; set; } = "";

        public double Score { get; set; }
    }

    private static byte[] Bytes(int length) => Enumerable.Range(0, length).Select(i => (byte)((i * 17) + (i / 256))).ToArray();

    private static async Task<List<StorageObject>> List(IParquetStorage storage, string prefix)
    {
        var objects = new List<StorageObject>();
        await foreach (var item in storage.ListAsync(prefix))
        {
            objects.Add(item);
        }

        return objects;
    }

    [Test]
    public async Task ItListsTheObjectsWithAPrefix_AcrossThePagesOfTheListing()
    {
        var (client, fake) = FakeS3.Create();
        fake.PageSize = 2;
        for (int i = 0; i < 5; i++)
        {
            fake.Put($"people/p{i}.parquet", Bytes(10 + i));
        }

        fake.Put("other/x.parquet", Bytes(3));
        var storage = new S3ParquetStorage(client, "my-bucket");

        var objects = await List(storage, "people/");

        await Assert.That(objects.Select(o => o.Key)).IsEquivalentTo(Enumerable.Range(0, 5).Select(i => $"people/p{i}.parquet"));
        await Assert.That(objects.Select(o => o.Length)).IsEquivalentTo(Enumerable.Range(10, 5).Select(i => (long)i));
        await Assert.That(fake.Calls.Count(c => c == "ListObjectsV2Async")).IsEqualTo(3);
        await Assert.That(fake.Bucket).IsEqualTo("my-bucket");
    }

    [Test]
    public async Task AnEmptyListing_IsEmpty()
    {
        var (client, _) = FakeS3.Create();

        await Assert.That((await List(new S3ParquetStorage(client, "b"), "nothing")).Count).IsEqualTo(0);
    }

    [Test]
    public async Task AnObjectIsOpenedWithItsSize_AndReadByRanges()
    {
        var (client, fake) = FakeS3.Create();
        var data = Bytes(5000);
        fake.Put("a.parquet", data, "\"abc\"");
        var storage = new S3ParquetStorage(client, "b", new RangeReadStreamOptions { BlockSize = 1000, TailSize = 0 });

        await using var stream = await storage.OpenReadAsync("a.parquet");
        stream.Position = 2500;
        var small = new byte[10];
        await stream.ReadExactlyAsync(small);
        stream.Position = 100;
        var large = new byte[3000];
        await stream.ReadExactlyAsync(large);

        await Assert.That(stream.Length).IsEqualTo(5000);
        await Assert.That(small.AsSpan().SequenceEqual(data.AsSpan(2500, 10))).IsTrue();
        await Assert.That(large.AsSpan().SequenceEqual(data.AsSpan(100, 3000))).IsTrue();

        // The first read is the block it is in, the second is exactly what it asked for; the ends are the last byte, as in the Range header.
        await Assert.That(fake.Ranges.Select(r => (r.Start, r.End))).IsEquivalentTo(new[] { (2000L, 2999L), (100L, 3099L) });
        await Assert.That(fake.Ranges.All(r => r.ETag == "\"abc\"")).IsTrue();
    }

    [Test]
    public async Task ARangeToTheEndOfTheObject_IsTheLastByte()
    {
        var (client, fake) = FakeS3.Create();
        var data = Bytes(2500);
        fake.Put("a", data);
        var storage = new S3ParquetStorage(client, "b", new RangeReadStreamOptions { BlockSize = 1000, TailSize = 0 });

        await using var stream = await storage.OpenReadAsync("a");
        stream.Position = 2400;
        var buffer = new byte[100];
        await stream.ReadExactlyAsync(buffer);

        await Assert.That(fake.Ranges.Single().Start).IsEqualTo(2000);
        await Assert.That(fake.Ranges.Single().End).IsEqualTo(2499);
        await Assert.That(buffer.AsSpan().SequenceEqual(data.AsSpan(2400, 100))).IsTrue();
    }

    [Test]
    public async Task AnObjectThatIsNotThere_IsFileNotFound()
    {
        var (client, _) = FakeS3.Create();
        var storage = new S3ParquetStorage(client, "b");

        await Assert.That(async () => await storage.OpenReadAsync("missing.parquet")).Throws<FileNotFoundException>();
    }

    [Test]
    public async Task AnObjectThatChangesWhileItIsRead_IsAnError_NotAFileMadeOfTwoVersions()
    {
        var (client, fake) = FakeS3.Create();
        fake.Put("a", Bytes(5000));
        var storage = new S3ParquetStorage(client, "b", new RangeReadStreamOptions { BlockSize = 1000, TailSize = 0 });

        await using var stream = await storage.OpenReadAsync("a");
        await stream.ReadExactlyAsync(new byte[10]);
        fake.Replace("a", Bytes(5000));
        stream.Position = 3000;

        await Assert.That(async () => await stream.ReadExactlyAsync(new byte[10])).Throws<IOException>();
    }

    [Test]
    public async Task TheArgumentsAreChecked()
    {
        var (client, _) = FakeS3.Create();

        await Assert.That(() => new S3ParquetStorage(null!, "b")).Throws<ArgumentNullException>();
        await Assert.That(() => new S3ParquetStorage(client, "")).Throws<ArgumentException>();
        await Assert.That(async () => await new S3ParquetStorage(client, "b").OpenReadAsync("")).Throws<ArgumentException>();
    }

    [Test]
    public async Task AParquetFileInTheBucket_IsReadByTheSerializer_WithoutFetchingTheRowGroupsAFilterRulesOut()
    {
        var (client, fake) = FakeS3.Create();
        var file = StorageSampleFile.Create(rowGroups: 20, rowsPerGroup: 2000);
        fake.Put("data.parquet", file);
        var storage = new S3ParquetStorage(client, "b", new RangeReadStreamOptions { BlockSize = 16 * 1024, TailSize = 16 * 1024 });
        int wanted = (13 * 2000) + 5;

        await using var stream = await storage.OpenReadAsync("data.parquet");
        var rows = await ParquetSerializer.DeserializeAsync<Reading>(stream, new ParquetDeserializerOptions { RowGroupFilter = ParquetFilter.Column("Id").Equal(wanted) });

        await Assert.That(rows.Count).IsEqualTo(2000);
        await Assert.That(rows.Single(r => r.Id == wanted).Name).IsEqualTo($"name-{wanted}");

        long fetched = fake.Ranges.Sum(r => r.End - r.Start + 1);
        await Assert.That(fetched).IsLessThan(file.Length / 5);
    }

    [Test]
    public async Task ADatasetOverABucket_ReadsTheFilesTheQueryNeeds()
    {
        var (client, fake) = FakeS3.Create();
        for (int i = 0; i < 4; i++)
        {
            fake.Put($"people/part-{i}.parquet", StorageSampleFile.Create(rowGroups: 5, rowsPerGroup: 1000, firstId: i * 100_000));
        }

        fake.Put("people/_SUCCESS", []);
        var storage = new S3ParquetStorage(client, "b", new RangeReadStreamOptions { BlockSize = 16 * 1024, TailSize = 16 * 1024 });

        var plan = ParquetDataset.Open<Reading>(storage, "people/").Where(r => r.Id >= 200_000 && r.Id < 200_010).Explain();
        var rows = ParquetDataset.Open<Reading>(storage, "people/").Where(r => r.Id >= 200_000 && r.Id < 200_010).Select(r => r.Name).ToList();

        await Assert.That(plan.Files).IsEqualTo(4);
        await Assert.That(plan.FilesSkipped).IsEqualTo(3);
        await Assert.That(rows.Order()).IsEquivalentTo(Enumerable.Range(200_000, 10).Select(i => $"name-{i}").Order());
    }
}
