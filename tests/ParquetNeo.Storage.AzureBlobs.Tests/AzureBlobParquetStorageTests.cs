using ParquetNeo.Linq;
using ParquetNeo.Serialization;
using ParquetNeo.Storage;
using ParquetNeo.Tests.Storage;

namespace ParquetNeo.Storage.AzureBlobs.Tests;

/// <summary>The storage of a container of Azure Blob Storage, against a container in memory that answers as Blob Storage does.</summary>
public class AzureBlobParquetStorageTests
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
    public async Task ItListsTheBlobsWithAPrefix_AcrossThePagesOfTheListing()
    {
        var container = new FakeContainer { PageSize = 2 };
        for (int i = 0; i < 5; i++)
        {
            container.Put($"people/p{i}.parquet", Bytes(10 + i));
        }

        container.Put("other/x.parquet", Bytes(3));
        var storage = new AzureBlobParquetStorage(container);

        var objects = await List(storage, "people/");

        await Assert.That(objects.Select(o => o.Key)).IsEquivalentTo(Enumerable.Range(0, 5).Select(i => $"people/p{i}.parquet"));
        await Assert.That(objects.Select(o => o.Length)).IsEquivalentTo(Enumerable.Range(10, 5).Select(i => (long)i));
        await Assert.That(container.LastPrefix).IsEqualTo("people/");
    }

    [Test]
    public async Task AnEmptyListing_IsEmpty()
    {
        var storage = new AzureBlobParquetStorage(new FakeContainer());

        await Assert.That((await List(storage, "nothing")).Count).IsEqualTo(0);
    }

    [Test]
    public async Task ABlobIsOpenedWithItsSize_AndReadByRanges()
    {
        var container = new FakeContainer();
        var data = Bytes(5000);
        container.Put("a.parquet", data, "\"abc\"");
        var storage = new AzureBlobParquetStorage(container, new RangeReadStreamOptions { BlockSize = 1000, TailSize = 0 });

        await using var stream = await storage.OpenReadAsync("a.parquet");
        stream.Position = 2500;
        var small = new byte[10];
        await stream.ReadExactlyAsync(small);
        stream.Position = 100;
        var large = new byte[3000];
        await stream.ReadExactlyAsync(large);

        await Assert.That(stream.Length).IsEqualTo(5000);
        await Assert.That(container.PropertyCalls).IsEqualTo(1);
        await Assert.That(small.AsSpan().SequenceEqual(data.AsSpan(2500, 10))).IsTrue();
        await Assert.That(large.AsSpan().SequenceEqual(data.AsSpan(100, 3000))).IsTrue();

        // The first read is the block it is in, the second is exactly what it asked for; each is tied to the version that was opened.
        await Assert.That(container.Ranges.Select(r => (r.Offset, r.Length))).IsEquivalentTo(new[] { (2000L, 1000L), (100L, 3000L) });
        await Assert.That(container.Ranges.All(r => r.IfMatch == new Azure.ETag("\"abc\""))).IsTrue();
    }

    [Test]
    public async Task TheLastBlock_IsAsShortAsTheBlob()
    {
        var container = new FakeContainer();
        var data = Bytes(2500);
        container.Put("a", data);
        var storage = new AzureBlobParquetStorage(container, new RangeReadStreamOptions { BlockSize = 1000, TailSize = 0 });

        await using var stream = await storage.OpenReadAsync("a");
        stream.Position = 2400;
        var buffer = new byte[100];
        await stream.ReadExactlyAsync(buffer);

        await Assert.That(container.Ranges.Single().Offset).IsEqualTo(2000);
        await Assert.That(container.Ranges.Single().Length).IsEqualTo(500);
        await Assert.That(buffer.AsSpan().SequenceEqual(data.AsSpan(2400, 100))).IsTrue();
    }

    [Test]
    public async Task ABlobThatIsNotThere_IsFileNotFound()
    {
        var storage = new AzureBlobParquetStorage(new FakeContainer());

        await Assert.That(async () => await storage.OpenReadAsync("missing.parquet")).Throws<FileNotFoundException>();
    }

    [Test]
    public async Task ABlobThatChangesWhileItIsRead_IsAnError_NotAFileMadeOfTwoVersions()
    {
        var container = new FakeContainer().Put("a", Bytes(5000));
        var storage = new AzureBlobParquetStorage(container, new RangeReadStreamOptions { BlockSize = 1000, TailSize = 0 });

        await using var stream = await storage.OpenReadAsync("a");
        await stream.ReadExactlyAsync(new byte[10]);
        container.Replace("a", Bytes(5000));
        stream.Position = 3000;

        await Assert.That(async () => await stream.ReadExactlyAsync(new byte[10])).Throws<IOException>();
    }

    [Test]
    public async Task TheArgumentsAreChecked()
    {
        await Assert.That(() => new AzureBlobParquetStorage(null!)).Throws<ArgumentNullException>();
        await Assert.That(async () => await new AzureBlobParquetStorage(new FakeContainer()).OpenReadAsync("")).Throws<ArgumentException>();
    }

    [Test]
    public async Task AParquetFileInTheContainer_IsReadByTheSerializer_WithoutFetchingTheRowGroupsAFilterRulesOut()
    {
        var container = new FakeContainer();
        var file = StorageSampleFile.Create(rowGroups: 20, rowsPerGroup: 2000);
        container.Put("data.parquet", file);
        var storage = new AzureBlobParquetStorage(container, new RangeReadStreamOptions { BlockSize = 16 * 1024, TailSize = 16 * 1024 });
        int wanted = (13 * 2000) + 5;

        await using var stream = await storage.OpenReadAsync("data.parquet");
        var rows = await ParquetSerializer.DeserializeAsync<Reading>(stream, new ParquetDeserializerOptions { RowGroupFilter = ParquetFilter.Column("Id").Equal(wanted) });

        await Assert.That(rows.Count).IsEqualTo(2000);
        await Assert.That(rows.Single(r => r.Id == wanted).Name).IsEqualTo($"name-{wanted}");
        await Assert.That(container.Ranges.Sum(r => r.Length)).IsLessThan(file.Length / 5);
    }

    [Test]
    public async Task ADatasetOverAContainer_ReadsTheFilesTheQueryNeeds()
    {
        var container = new FakeContainer();
        for (int i = 0; i < 4; i++)
        {
            container.Put($"people/part-{i}.parquet", StorageSampleFile.Create(rowGroups: 5, rowsPerGroup: 1000, firstId: i * 100_000));
        }

        container.Put("people/_SUCCESS", []);
        var storage = new AzureBlobParquetStorage(container, new RangeReadStreamOptions { BlockSize = 16 * 1024, TailSize = 16 * 1024 });

        var plan = ParquetDataset.Open<Reading>(storage, "people/").Where(r => r.Id >= 200_000 && r.Id < 200_010).Explain();
        var rows = ParquetDataset.Open<Reading>(storage, "people/").Where(r => r.Id >= 200_000 && r.Id < 200_010).Select(r => r.Name).ToList();

        await Assert.That(plan.Files).IsEqualTo(4);
        await Assert.That(plan.FilesSkipped).IsEqualTo(3);
        await Assert.That(rows.Order()).IsEquivalentTo(Enumerable.Range(200_000, 10).Select(i => $"name-{i}").Order());
    }
}
