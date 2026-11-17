using ParquetNeo.Data;
using ParquetNeo.Exceptions;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;
using ParquetNeo.Tests.Storage;

namespace ParquetNeo.Tests.Async;

public class AsyncReaderTests
{
    private static readonly byte[] File = StorageSampleFile.Create(rowGroups: 4, rowsPerGroup: 2000);

    private static AsyncOnlyStream Open(byte[]? bytes = null) => new(new MemoryStream(bytes ?? File));

    [Test]
    public async Task OpenAsync_ReadsTheFooterWithoutBlocking()
    {
        var stream = Open();

        await using var reader = await ParquetFileReader.OpenAsync(stream);

        await Assert.That(reader.RowCount).IsEqualTo(8000);
        await Assert.That(reader.RowGroupCount).IsEqualTo(4);
        await Assert.That(stream.AsyncReads).IsGreaterThan(0);
    }

    [Test]
    public async Task OpenColumnAsync_ReadsTheValuesFromMemory()
    {
        // The column chunk is read in one asynchronous read, so reading the values never touches the stream (which fails on a synchronous read).
        await using var reader = await ParquetFileReader.OpenAsync(Open());

        using var column = await reader.OpenRowGroup(1).OpenColumnAsync("Id");
        var ids = new int[2000];
        await Assert.That(column.ReadBatch(ids).Entries).IsEqualTo(2000);
        await Assert.That(ids[0]).IsEqualTo(2000);
        await Assert.That(ids[^1]).IsEqualTo(3999);
    }

    [Test]
    public async Task OpenColumnAsync_WithASelection_ReadsOnlyTheSelectedRows()
    {
        await using var reader = await ParquetFileReader.OpenAsync(Open());
        var selection = new RowSelection([new RowRange(1500, 10)]);

        using var column = await reader.OpenRowGroup(0).OpenColumnAsync(0, selection);
        var ids = new int[10];
        await Assert.That(column.ReadBatch(ids).Entries).IsEqualTo(10);
        await Assert.That(ids[0]).IsEqualTo(1500);
        await Assert.That(ids[9]).IsEqualTo(1509);
    }

    [Test]
    public async Task OpenColumnAsync_WithASelectionFromAFileWithoutAPageIndex_Throws()
    {
        var memory = new MemoryStream();
        using (var writer = ParquetFileWriter.Open(memory, new ParquetSchema([new PrimitiveNode("a", PhysicalType.Int32, FieldRepetitionType.Required)]), leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            group.NextColumn().WriteBatch(new[] { 1, 2, 3 });
        }

        await using var reader = await ParquetFileReader.OpenAsync(Open(memory.ToArray()));

        await Assert.That(async () => await reader.OpenRowGroup(0).OpenColumnAsync(0, new RowSelection([new RowRange(0, 1)]))).Throws<NotSupportedException>();
    }

    [Test]
    public async Task OpenColumnAsync_WithAnUnknownName_Throws()
    {
        await using var reader = await ParquetFileReader.OpenAsync(Open());

        await Assert.That(async () => await reader.OpenRowGroup(0).OpenColumnAsync("Nope")).Throws<ArgumentException>();
        await Assert.That(async () => await reader.OpenRowGroup(0).OpenColumnAsync(7)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task GetMatchingRowGroupsAsync_GivesTheSameRowGroupsAsTheSynchronousMethod()
    {
        var filter = ParquetFilter.Column("Id").Equal(4500);
        await using var reader = await ParquetFileReader.OpenAsync(Open());

        var matching = await reader.GetMatchingRowGroupsAsync(filter);

        await Assert.That(matching.SequenceEqual(ParquetFileReader.Open(new MemoryStream(File)).GetMatchingRowGroups(filter))).IsTrue();
        await Assert.That(matching.Count).IsEqualTo(1);
    }

    [Test]
    public async Task SelectRowsAsync_GivesTheSameSelectionAsTheSynchronousMethod()
    {
        var filter = ParquetFilter.Column("Id").Between(10, 20);
        await using var reader = await ParquetFileReader.OpenAsync(Open());

        var selection = await reader.OpenRowGroup(0).SelectRowsAsync(filter, [0, 1]);

        using var other = ParquetFileReader.Open(new MemoryStream(File));
        var expected = other.OpenRowGroup(0).SelectRows(filter, [0, 1]);
        await Assert.That(selection).IsNotNull();
        await Assert.That(selection!.Ranges.SequenceEqual(expected!.Ranges)).IsTrue();
        await Assert.That(async () => await reader.OpenRowGroup(0).SelectRowsAsync(filter, [9])).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ParquetDataReader_ReadAsync_ReadsAllRows_WithoutBlocking()
    {
        await using var reader = await ParquetDataReader.OpenAsync(Open());

        int rows = 0;
        long sum = 0;
        while (await reader.ReadAsync())
        {
            rows++;
            sum += reader.GetInt32(0);
        }

        await Assert.That(rows).IsEqualTo(8000);
        await Assert.That(sum).IsEqualTo(Enumerable.Range(0, 8000).Sum(i => (long)i));
        await Assert.That(reader.GetOrdinal("Name")).IsEqualTo(1);
    }

    [Test]
    public async Task ParquetDataReader_WithAFilter_ReadsOnlyTheMatchingRows_Asynchronously()
    {
        var options = new ParquetDataReaderOptions { RowGroupFilter = ParquetFilter.Column("Id").Equal(4500) };
        await using var reader = await ParquetDataReader.OpenAsync(Open(), options);

        int rows = 0;
        while (await reader.ReadAsync())
        {
            rows++;
        }

        await Assert.That(rows).IsLessThan(8000);
        await Assert.That(rows).IsGreaterThan(0);
    }

    [Test]
    public async Task ParquetDataReader_CloseAsync_ClosesTheReader()
    {
        var reader = await ParquetDataReader.OpenAsync(Open());

        await reader.CloseAsync();
        await reader.CloseAsync();

        await Assert.That(reader.IsClosed).IsTrue();
        await Assert.That(async () => await reader.ReadAsync()).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task ParquetDataReader_DisposeAsync_ClosesTheStream()
    {
        var memory = new MemoryStream(File);
        var reader = await ParquetDataReader.OpenAsync(memory);

        await reader.DisposeAsync();

        await Assert.That(reader.IsClosed).IsTrue();
        await Assert.That(memory.CanRead).IsFalse();
    }

    [Test]
    public async Task OpenAsync_OfAFileThatIsNotParquet_Throws_AndClosesTheStream()
    {
        var memory = new MemoryStream(new byte[100]);

        await Assert.That(async () => await ParquetFileReader.OpenAsync(memory)).Throws<ParquetFormatException>();

        await Assert.That(memory.CanRead).IsFalse();
    }

    [Test]
    public async Task OpenAsync_OfATooShortFile_Throws()
    {
        await Assert.That(async () => await ParquetFileReader.OpenAsync(new MemoryStream(new byte[5]), leaveOpen: true)).Throws<ParquetFormatException>();
    }
}
