using ParquetNeo.Storage;

namespace ParquetNeo.Tests.Storage;

/// <summary>The file reader over a stream of a storage reads the parts of the file that are asked for, not the file.</summary>
public class ReadingFromAStorageTests
{
    private const int Groups = 20;
    private const int Rows = 2000;

    private static (InMemoryStorage Storage, byte[] File) Setup()
    {
        var file = StorageSampleFile.Create(Groups, Rows);
        return (new InMemoryStorage { Options = new RangeReadStreamOptions { BlockSize = 64 * 1024, TailSize = 16 * 1024 } }.Put("data.parquet", file), file);
    }

    [Test]
    public async Task TheFooter_IsReadWithAFewRequests()
    {
        var (storage, file) = Setup();

        await using var stream = await storage.OpenReadAsync("data.parquet");
        using var reader = new ParquetFileReader(stream, leaveOpen: true);

        await Assert.That(reader.RowGroupCount).IsEqualTo(Groups);
        await Assert.That(reader.RowCount).IsEqualTo(Groups * Rows);
        await Assert.That(storage.Requests.Count).IsLessThanOrEqualTo(2);
        await Assert.That(storage.BytesRead).IsLessThan(file.Length / 4);
    }

    [Test]
    public async Task OneColumnOfOneRowGroup_IsReadFromItsOwnBytes()
    {
        var (storage, file) = Setup();

        await using var stream = await storage.OpenReadAsync("data.parquet");
        using var reader = new ParquetFileReader(stream, leaveOpen: true);
        var ids = new int[Rows];
        using (var column = reader.OpenRowGroup(7).OpenColumn("Id"))
        {
            int read = column.ReadBatch(ids).Entries;
            await Assert.That(read).IsEqualTo(Rows);
        }

        await Assert.That(ids[0]).IsEqualTo(7 * Rows);
        await Assert.That(ids[^1]).IsEqualTo((8 * Rows) - 1);
        await Assert.That(storage.BytesRead).IsLessThan(file.Length / 3);
    }

    [Test]
    public async Task AFilterWithAPageIndex_ReadsOnlyTheRowsThatMayMatch()
    {
        var (storage, file) = Setup();
        int wanted = (11 * Rows) + 700;

        await using var stream = await storage.OpenReadAsync("data.parquet");
        using var reader = new ParquetFileReader(stream, leaveOpen: true);
        var filter = ParquetFilter.Column("Id").Equal(wanted);
        var groups = reader.GetMatchingRowGroups(filter);
        await Assert.That(groups).IsEquivalentTo(new[] { 11 });

        var group = reader.OpenRowGroup(11);
        var selection = group.SelectRows(filter, [0, 1])!;
        await Assert.That(selection.RowCount).IsLessThan(Rows);
        var names = new List<string>();
        using (var column = group.OpenColumn(1, selection))
        {
            var buffer = new ReadOnlyMemory<byte>[500];
            BatchRead read;
            while ((read = column.ReadByteArrays(buffer)).Entries > 0)
            {
                names.AddRange(buffer.Take(read.Entries).Select(b => System.Text.Encoding.UTF8.GetString(b.Span)));
            }
        }

        await Assert.That(names).Contains($"name-{wanted}");
        await Assert.That(storage.BytesRead).IsLessThan(file.Length / 6);
    }

    [Test]
    public async Task ASlowStorage_ThatGivesLittleAtATime_ReadsTheSameRows()
    {
        var (storage, _) = Setup();
        storage.MaxBytesPerRead = 777;

        await using var stream = await storage.OpenReadAsync("data.parquet");
        using var reader = new ParquetFileReader(stream, leaveOpen: true);
        var names = new List<string>();
        using (var column = reader.OpenRowGroup(3).OpenColumn("Name"))
        {
            var buffer = new ReadOnlyMemory<byte>[500];
            BatchRead read;
            while ((read = column.ReadByteArrays(buffer)).Entries > 0)
            {
                names.AddRange(buffer.Take(read.Entries).Select(b => System.Text.Encoding.UTF8.GetString(b.Span)));
            }
        }

        await Assert.That(names.Count).IsEqualTo(Rows);
        await Assert.That(names[0]).IsEqualTo($"name-{3 * Rows}");
    }
}
