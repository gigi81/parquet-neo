using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Async;

public class AsyncWriterTests
{
    private const int Rows = 5000;

    private static readonly ParquetSchema Schema = new(
    [
        new PrimitiveNode("id", PhysicalType.Int64, FieldRepetitionType.Required),
        new PrimitiveNode("name", PhysicalType.ByteArray, FieldRepetitionType.Required, LogicalType.String),
        new PrimitiveNode("score", PhysicalType.Double, FieldRepetitionType.Required),
    ]);

    private static readonly long[] Ids = Enumerable.Range(0, Rows).Select(i => (long)i).ToArray();
    private static readonly string[] Names = Enumerable.Range(0, Rows).Select(i => "name-" + (i % 100)).ToArray();
    private static readonly double[] Scores = Enumerable.Range(0, Rows).Select(i => i / 2.0).ToArray();

    private static readonly Action<ColumnWriter>[] Columns =
    [
        column => column.WriteBatch(Ids),
        column => column.WriteStrings(Names),
        column => column.WriteBatch(Scores),
    ];

    private static ParquetWriterOptions Options { get; } = new()
    {
        CreatedBy = "test",
        DataPageSize = 4000,
        WritePageIndex = true,
        ColumnBloomFilters = new Dictionary<string, BloomFilterSettings> { ["id"] = new(), ["name"] = new() },
    };

    private static byte[] Synchronous(int groups)
    {
        var stream = new MemoryStream();
        using (var writer = ParquetFileWriter.Create(stream, Schema, Options, leaveOpen: true))
        {
            for (int g = 0; g < groups; g++)
            {
                using var group = writer.AppendRowGroup();
                foreach (var write in Columns)
                {
                    write(group.NextColumn());
                }
            }
        }

        return stream.ToArray();
    }

    [Test]
    public async Task WriteColumnsAsync_MakesTheFileOfTheSynchronousWriter_WithoutBlockingOnTheStream()
    {
        var memory = new MemoryStream();
        var stream = new AsyncOnlyStream(memory);

        var writer = await ParquetFileWriter.CreateAsync(stream, Schema, Options, leaveOpen: true);
        for (int g = 0; g < 3; g++)
        {
            await using var group = writer.AppendRowGroup();
            await group.WriteColumnsAsync(Columns);
        }

        await writer.DisposeAsync();

        await Assert.That(memory.ToArray().AsSpan().SequenceEqual(Synchronous(3))).IsTrue();
        await Assert.That(stream.AsyncWrites).IsGreaterThan(3);
    }

    [Test]
    public async Task WriteColumnAsync_OneColumnAtATime_MakesTheSameFile()
    {
        var memory = new MemoryStream();
        var stream = new AsyncOnlyStream(memory);

        await using (var writer = await ParquetFileWriter.CreateAsync(stream, Schema, Options, leaveOpen: true))
        {
            for (int g = 0; g < 2; g++)
            {
                var group = writer.AppendRowGroup();
                foreach (var write in Columns)
                {
                    await group.WriteColumnAsync(write);
                }

                await group.CloseAsync();
            }

            await writer.CloseAsync();
        }

        await Assert.That(memory.ToArray().AsSpan().SequenceEqual(Synchronous(2))).IsTrue();
    }

    [Test]
    public async Task TheFileThatIsWrittenAsynchronously_IsRead()
    {
        var memory = new MemoryStream();
        await using (var writer = await ParquetFileWriter.CreateAsync(memory, Schema, Options, leaveOpen: true))
        {
            await using var group = writer.AppendRowGroup();
            await group.WriteColumnsAsync(Columns, maxDegreeOfParallelism: 2);
        }

        memory.Position = 0;
        using var reader = ParquetFileReader.Open(memory);
        using var column = reader.OpenRowGroup(0).OpenColumn(0);
        var read = new long[Rows];
        await Assert.That(column.ReadBatch(read).Entries).IsEqualTo(Rows);
        await Assert.That(read.AsSpan().SequenceEqual(Ids)).IsTrue();
    }

    [Test]
    public async Task DisposeAsyncOfTheWriter_WritesThePageIndexes()
    {
        var memory = new MemoryStream();
        await using (var writer = await ParquetFileWriter.CreateAsync(memory, Schema, Options, leaveOpen: true))
        {
            await using var group = writer.AppendRowGroup();
            await group.WriteColumnsAsync(Columns);
        }

        using var reader = ParquetFileReader.Open(new MemoryStream(memory.ToArray()));
        await Assert.That(reader.Metadata.RowGroups[0].Columns[0].OffsetIndexOffset).IsNotNull();
        await Assert.That(reader.Metadata.RowGroups[0].Columns[0].MetaData!.BloomFilterOffset).IsNotNull();
    }

    [Test]
    public async Task WriteColumnsAsync_WhenAnActionThrows_ThrowsItAndDisposingTheGroupAbandonsTheFile()
    {
        var memory = new MemoryStream();
        var writer = await ParquetFileWriter.CreateAsync(memory, Schema, Options, leaveOpen: true);
        var group = writer.AppendRowGroup();
        Action<ColumnWriter>[] broken = [Columns[0], _ => throw new FormatException("boom"), Columns[2]];

        await Assert.That(async () => await group.WriteColumnsAsync(broken)).Throws<FormatException>();
        await group.DisposeAsync();

        await Assert.That(async () => await writer.CloseAsync()).Throws<InvalidOperationException>();
        await writer.DisposeAsync();
    }

    [Test]
    public async Task WriteColumnsAsync_WhenCancelled_ThrowsOperationCanceled()
    {
        var writer = await ParquetFileWriter.CreateAsync(new MemoryStream(), Schema, Options);
        var group = writer.AppendRowGroup();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.That(async () => await group.WriteColumnsAsync(Columns, cancelled.Token)).Throws<OperationCanceledException>();
        await group.DisposeAsync();
        await writer.DisposeAsync();
    }

    [Test]
    public async Task WriteColumnAsync_AfterTheLastColumn_Throws()
    {
        await using var writer = await ParquetFileWriter.CreateAsync(new MemoryStream(), Schema, Options);
        await using var group = writer.AppendRowGroup();
        foreach (var write in Columns)
        {
            await group.WriteColumnAsync(write);
        }

        await Assert.That(async () => await group.WriteColumnAsync(Columns[0])).Throws<InvalidOperationException>();
        await Assert.That(async () => await group.WriteColumnAsync(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task WriteColumnAsync_WithDifferentRowCounts_Throws()
    {
        await using var writer = await ParquetFileWriter.CreateAsync(new MemoryStream(), Schema, Options);
        var group = writer.AppendRowGroup();
        await group.WriteColumnAsync(Columns[0]);

        await Assert.That(async () => await group.WriteColumnAsync(column => column.WriteStrings(["a", "b"]))).Throws<InvalidOperationException>();
        await group.DisposeAsync();
    }

    [Test]
    public async Task CloseAsync_WhileARowGroupIsOpen_Throws_AndAfterClosingDoesNothing()
    {
        var writer = await ParquetFileWriter.CreateAsync(new MemoryStream(), Schema, Options);
        var group = writer.AppendRowGroup();
        await Assert.That(async () => await writer.CloseAsync()).Throws<InvalidOperationException>();

        await group.WriteColumnsAsync(Columns);
        await group.CloseAsync();
        await group.CloseAsync();
        await writer.CloseAsync();
        await writer.CloseAsync();
    }

    [Test]
    public async Task DisposeAsyncOfAnIncompleteGroup_AbandonsTheFile()
    {
        var writer = await ParquetFileWriter.CreateAsync(new MemoryStream(), Schema, Options);
        var group = writer.AppendRowGroup();
        await group.WriteColumnAsync(Columns[0]);

        await group.DisposeAsync();
        await group.DisposeAsync();

        await Assert.That(async () => await writer.CloseAsync()).Throws<InvalidOperationException>();
        await writer.DisposeAsync();
    }

    [Test]
    public async Task WriteColumnAsync_WhenTheColumnThrows_ThrowsIt()
    {
        await using var writer = await ParquetFileWriter.CreateAsync(new MemoryStream(), Schema, Options);
        var group = writer.AppendRowGroup();

        await Assert.That(async () => await group.WriteColumnAsync(_ => throw new FormatException("boom"))).Throws<FormatException>();
        await group.DisposeAsync();
    }

    [Test]
    public async Task CreateAsync_WhenTheStreamFails_Throws()
    {
        await Assert.That(async () => await ParquetFileWriter.CreateAsync(new FailingStream(), Schema, Options)).Throws<IOException>();
    }

    private sealed class FailingStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("disk full");
    }
}
