using ParquetNeo.Storage;

namespace ParquetNeo.Tests.Storage;

/// <summary>A folder of a disk as a storage.</summary>
public class LocalFileStorageTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parquet-neo-storage-" + Guid.NewGuid().ToString("N"));

    public LocalFileStorageTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "people", "2024"));
        File.WriteAllBytes(Path.Combine(_root, "people", "a.parquet"), [1, 2, 3]);
        File.WriteAllBytes(Path.Combine(_root, "people", "2024", "b.parquet"), [4, 5, 6, 7]);
        File.WriteAllBytes(Path.Combine(_root, "other.parquet"), [9]);
    }

    public void Dispose()
    {
        Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static async Task<List<StorageObject>> List(IParquetStorage storage, string prefix)
    {
        var objects = new List<StorageObject>();
        await foreach (var item in storage.ListAsync(prefix))
        {
            objects.Add(item);
        }

        return objects.OrderBy(o => o.Key, StringComparer.Ordinal).ToList();
    }

    [Test]
    public async Task ItListsTheFilesBelowAPrefix_WithForwardSlashes()
    {
        var storage = new LocalFileStorage(_root);

        var people = await List(storage, "people/");
        var all = await List(storage, "");

        await Assert.That(people.Select(o => o.Key)).IsEquivalentTo(new[] { "people/2024/b.parquet", "people/a.parquet" });
        await Assert.That(people.Select(o => o.Length)).IsEquivalentTo(new long[] { 4, 3 });
        await Assert.That(all.Count).IsEqualTo(3);
        await Assert.That((await List(storage, "nothing")).Count).IsEqualTo(0);
    }

    [Test]
    public async Task ItOpensAFileAsAStreamThatSeeks()
    {
        var storage = new LocalFileStorage(_root);

        await using var stream = await storage.OpenReadAsync("people/2024/b.parquet");
        stream.Position = 1;
        var buffer = new byte[3];
        await stream.ReadExactlyAsync(buffer);

        await Assert.That(buffer).IsEquivalentTo(new byte[] { 5, 6, 7 });
        await Assert.That(stream.Length).IsEqualTo(4);
    }

    [Test]
    public async Task AKeyOutsideTheFolder_AndAMissingFile_AreErrors()
    {
        var storage = new LocalFileStorage(Path.Combine(_root, "people"));

        await Assert.That(async () => await storage.OpenReadAsync("../other.parquet")).Throws<ArgumentException>();
        await Assert.That(async () => await storage.OpenReadAsync("nope.parquet")).Throws<FileNotFoundException>();
    }

    [Test]
    public async Task AFolderThatIsNotThere_HasNoFiles()
    {
        var storage = new LocalFileStorage(Path.Combine(_root, "missing"));

        await Assert.That((await List(storage, "")).Count).IsEqualTo(0);
    }
}
