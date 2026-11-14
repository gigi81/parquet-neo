using ParquetNeo.Serialization;
using ParquetNeo.Storage;
using ParquetNeo.Tests.Storage;

namespace ParquetNeo.Linq.Tests;

/// <summary>Datasets over the files of a storage (an object storage, in the real world): the rows, what is skipped, and how little of the files is read.</summary>
public class StorageDatasetTests
{
    private const int Files = 4;
    private const int RowsPerFile = 400;

    /// <summary>Four files of 400 people in row groups of 100, under <c>people/</c>, ids in order as in <see cref="TestDataset"/>; and some other objects.</summary>
    private static InMemoryStorage Setup(RangeReadStreamOptions? options = null)
    {
        var storage = new InMemoryStorage { Options = options ?? new RangeReadStreamOptions { BlockSize = 2 * 1024, TailSize = 6 * 1024 } };
        for (int file = 0; file < Files; file++)
        {
            var stream = new MemoryStream();
            ParquetSerializer.Serialize(Enumerable.Range(0, RowsPerFile).Select(i => TestDataset.Make((file * 1000) + i)).ToList(), stream, new ParquetSerializerOptions { RowGroupSize = 100 });
            storage.Put($"people/people-{file}.parquet", stream.ToArray());
        }

        storage.Put("people/readme.txt", [1, 2, 3]);
        storage.Put("other/elsewhere.parquet", [4, 5, 6]);
        return storage;
    }

    private static long FilesSize(InMemoryStorage storage) => storage.ListAsync("people/").ToBlockingEnumerable().Where(o => o.Key.EndsWith(".parquet", StringComparison.Ordinal)).Sum(o => o.Length);

    [Test]
    public async Task TheRowsOfTheFilesBelowThePrefix_AreTheRows()
    {
        var storage = Setup();

        var ids = ParquetDataset.Open<Person>(storage, "people/").Select(p => p.Id).ToList();

        await Assert.That(ids.Count).IsEqualTo(Files * RowsPerFile);
        await Assert.That(ids.Order().ToArray()).IsEquivalentTo(Enumerable.Range(0, Files).SelectMany(f => Enumerable.Range(f * 1000, RowsPerFile)).ToArray());
    }

    [Test]
    public async Task AWhere_GivesTheRowsOfTheMemory_AndReadsLittleOfTheFiles()
    {
        var storage = Setup();
        using var local = new TestDataset();

        var rows = ParquetDataset.Open<Person>(storage, "people/").Where(p => p.Id >= 2000 && p.Id < 2150 && p.Name != null).OrderBy(p => p.Id).ToList();
        var expected = local.All.Where(p => p.Id >= 2000 && p.Id < 2150 && p.Name != null).OrderBy(p => p.Id).ToList();

        await Assert.That(rows.Select(p => p.Id)).IsEquivalentTo(expected.Select(p => p.Id));
        await Assert.That(rows.Select(p => p.Name)).IsEquivalentTo(expected.Select(p => p.Name));

        // The footers of the 4 files, and the first two row groups of one: a part of the files.
        await Assert.That(storage.BytesRead).IsLessThan(FilesSize(storage) / 2);
    }

    [Test]
    public async Task AQueryThatSelectsSomeColumns_ReadsLessThanOneThatSelectsAll()
    {
        var narrow = Setup();
        var wide = Setup();

        var ids = ParquetDataset.Open<Person>(narrow, "people/").Select(p => p.Id).ToList();
        var all = ParquetDataset.Open<Person>(wide, "people/").ToList();

        await Assert.That(ids.Count).IsEqualTo(all.Count);
        
        // Files this small are mostly footer and a block at a time, so the saving is less than the share of the columns.
        await Assert.That(narrow.BytesRead).IsLessThan(wide.BytesRead * 6 / 10);
    }

    [Test]
    public async Task TheNumberOfRows_IsAnsweredFromTheFooters()
    {
        var storage = Setup();

        int count = ParquetDataset.Open<Person>(storage, "people/").Count();

        await Assert.That(count).IsEqualTo(Files * RowsPerFile);
        
        // The beginning and the end of each file: its first block and its tail.
        await Assert.That(storage.Requests.Count).IsEqualTo(Files * 2);
        await Assert.That(storage.BytesRead).IsLessThan(FilesSize(storage) / 2);
    }

    [Test]
    public async Task ThePlan_SaysWhichFilesAreSkipped()
    {
        var storage = Setup();

        var plan = ParquetDataset.Open<Person>(storage, "people/").Where(p => p.Id >= 2000 && p.Id < 2150).Explain();

        await Assert.That(plan.Files).IsEqualTo(4);
        await Assert.That(plan.FilesSkipped).IsEqualTo(3);
        await Assert.That(plan.RowGroupsSkipped).IsEqualTo(2);
    }

    [Test]
    public async Task OnlyTheFilesThatMatchThePatternAndThePrefix_AreRead()
    {
        var storage = Setup();
        storage.Put("people/folder/deeper.parquet", Setup().ListAsync("people/").ToBlockingEnumerable().Count() > 0 ? ReadAll(Setup(), "people/people-0.parquet") : []);

        var all = ParquetDataset.Open<Person>(storage, "people/").Count();
        var top = ParquetDataset.Open<Person>(storage, "people/", new ParquetDatasetOptions { Recursive = false }).Count();
        var one = ParquetDataset.Open<Person>(storage, "people/people-3").Count();
        var pattern = ParquetDataset.Open<Person>(storage, "people/", new ParquetDatasetOptions { SearchPattern = "people-1*" }).Count();

        await Assert.That(all).IsEqualTo((Files + 1) * RowsPerFile);
        await Assert.That(top).IsEqualTo(Files * RowsPerFile);
        await Assert.That(one).IsEqualTo(RowsPerFile);
        await Assert.That(pattern).IsEqualTo(RowsPerFile);
    }

    [Test]
    public async Task APrefixWithNoFiles_IsAnEmptyDataset()
    {
        var storage = Setup();

        var rows = ParquetDataset.Open<Person>(storage, "nothing/").ToList();

        await Assert.That(rows).IsEmpty();
    }

    [Test]
    public async Task TheAsynchronousMethods_Work()
    {
        var storage = Setup();
        var people = ParquetDataset.Open<Person>(storage, "people/");

        var list = await people.Where(p => p.Id >= 3000 && p.Id < 3010).ToListAsync();
        var count = await people.CountAsync();
        var any = await people.Where(p => p.Id == 1234).AnyAsync();

        await Assert.That(list.Count).IsEqualTo(10);
        await Assert.That(count).IsEqualTo(Files * RowsPerFile);
        await Assert.That(any).IsTrue();
    }

    [Test]
    public async Task ADatasetOfAStorage_HasThePrefixAsItsPath()
    {
        var storage = Setup();

        await Assert.That(ParquetDataset.Open<Person>(storage, "people/").Path).IsEqualTo("people/");
        await Assert.That(() => ParquetDataset.Open<Person>(null!, "people/")).Throws<ArgumentNullException>();
    }

    private static byte[] ReadAll(InMemoryStorage storage, string key)
    {
        using var stream = storage.OpenReadAsync(key).AsTask().GetAwaiter().GetResult();
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }
}
