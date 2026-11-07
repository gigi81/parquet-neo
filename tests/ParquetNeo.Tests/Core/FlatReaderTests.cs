using ParquetNeo.Core;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Core;

public class FlatReaderTests
{
    [Test]
    [Arguments(false, 1000)]
    [Arguments(true, 1000)]
    [Arguments(true, 100_000)]
    [Arguments(true, 7)]
    [Arguments(true, 1)]
    [Arguments(false, 333)]
    public async Task EveryColumnType_ReadsBackWhatTheWriterWrote(bool optional, int batchSize)
    {
        using var reader = ReadHelper.Open(SampleFile.Build(optional, rows: 1000));

        for (int column = 0; column < SampleFile.ColumnNames.Length; column++)
        {
            var actual = ReadHelper.ReadColumn(reader, 0, column, batchSize);

            await Assert.That(SampleFile.FirstDifference(actual, optional, 0, 1000, column)).IsNull();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ManyPages_AreReadAcrossBatchBoundaries(bool optional)
    {
        var bytes = SampleFile.Build(optional, rows: 3000, options: new ParquetWriterOptions { DataPageSize = 100 });
        using var reader = ReadHelper.Open(bytes);

        foreach (int batchSize in new[] { 1, 13, 250, 1024, 5000 })
        {
            for (int column = 0; column < SampleFile.ColumnNames.Length; column++)
            {
                var actual = ReadHelper.ReadColumn(reader, 0, column, batchSize);

                await Assert.That(SampleFile.FirstDifference(actual, optional, 0, 3000, column)).IsNull().Because($"{SampleFile.ColumnNames[column]} batch {batchSize}");
            }
        }
    }

    [Test]
    public async Task EveryCodec_IsReadBack()
    {
        foreach (var codec in new[] { CompressionCodec.Gzip, CompressionCodec.Brotli, CompressionCodec.Snappy, CompressionCodec.Zstd, CompressionCodec.Lz4Raw })
        {
            var options = new ParquetWriterOptions { Compression = codec, Codecs = TestParquetFile.Registry, DataPageSize = 3000 };
            using var reader = ReadHelper.Open(SampleFile.Build(optional: true, rows: 2000, groups: 2, options: options));

            for (int g = 0; g < 2; g++)
            {
                for (int column = 0; column < SampleFile.ColumnNames.Length; column++)
                {
                    var actual = ReadHelper.ReadColumn(reader, g, column, 77);

                    await Assert.That(SampleFile.FirstDifference(actual, true, g * 1000, 1000, column)).IsNull().Because($"{codec}/{column}");
                }
            }
        }
    }

    [Test]
    public async Task MultipleRowGroups_AreReadIndependently()
    {
        using var reader = ReadHelper.Open(SampleFile.Build(optional: true, rows: 3001, groups: 3));

        await Assert.That(reader.RowGroupCount).IsEqualTo(3);
        await Assert.That(reader.RowCount).IsEqualTo(3001L);
        for (int g = 2; g >= 0; g--) // out of order on purpose
        {
            int from = g * 1000;
            int count = g == 2 ? 1001 : 1000;
            var group = reader.OpenRowGroup(g);
            await Assert.That(group.RowCount).IsEqualTo((long)count);
            await Assert.That(SampleFile.FirstDifference(ReadHelper.ReadColumn(reader, g, 5, 200), true, from, count, 5)).IsNull();
        }
    }

    [Test]
    public async Task TheFileExposesItsMetadataAndSchema()
    {
        using var reader = ReadHelper.Open(SampleFile.Build(optional: true, rows: 50, options: new ParquetWriterOptions { CreatedBy = "reader test" }));

        await Assert.That(reader.Metadata.CreatedBy).IsEqualTo("reader test");
        await Assert.That(reader.Schema.Columns.Count).IsEqualTo(13);
        await Assert.That(reader.Schema.FindColumn(["text"])!.LogicalType).IsEqualTo(LogicalType.String);
        await Assert.That(reader.OpenRowGroup(0).ColumnCount).IsEqualTo(13);
    }

    [Test]
    public async Task ColumnsCanBeOpenedByNameOrIndex()
    {
        using var reader = ReadHelper.Open(SampleFile.Build(optional: false, rows: 20));
        var group = reader.OpenRowGroup(0);

        using var byName = group.OpenColumn("i64");
        using var byIndex = group.OpenColumn(1);

        await Assert.That(byName.Column.Name).IsEqualTo("i64");
        await Assert.That(byIndex.Column).IsSameReferenceAs(byName.Column);
        await Assert.That(byName.RowCount).IsEqualTo(20L);
        await Assert.That(() => group.OpenColumn("missing")).Throws<ArgumentException>();
        await Assert.That(() => group.OpenColumn(13)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => group.OpenColumn(-1)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => reader.OpenRowGroup(1)).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task AnExhaustedColumn_KeepsReturningNoRows()
    {
        using var reader = ReadHelper.Open(SampleFile.Build(optional: false, rows: 10));
        using var column = reader.OpenRowGroup(0).OpenColumn(0);
        var values = new int[100];

        var first = column.ReadBatch(values);
        var second = column.ReadBatch(values);
        var third = column.ReadBatch(values);

        await Assert.That(first).IsEqualTo(new BatchRead(10, 10));
        await Assert.That(second).IsEqualTo(new BatchRead(0, 0));
        await Assert.That(third).IsEqualTo(new BatchRead(0, 0));
        await Assert.That(column.RowsRead).IsEqualTo(10L);
    }

    [Test]
    public async Task ABatchSmallerThanTheColumn_ResumesWhereItStopped()
    {
        using var reader = ReadHelper.Open(SampleFile.Build(optional: false, rows: 25));
        using var column = reader.OpenRowGroup(0).OpenColumn(0);
        var values = new int[10];

        var batches = new List<int[]>();
        BatchRead read;
        while ((read = column.ReadBatch(values)).Rows > 0)
        {
            batches.Add(values[..read.Rows]);
        }

        await Assert.That(batches.Select(b => b.Length)).IsEquivalentTo(new[] { 10, 10, 5 });
        await Assert.That(batches.SelectMany(b => b)).IsEquivalentTo(Enumerable.Range(0, 25).Select(i => i * 3 - 500));
    }

    [Test]
    public async Task OptionalBatches_ReportRowsAndValuesSeparately()
    {
        using var reader = ReadHelper.Open(SampleFile.Build(optional: true, rows: 100));
        using var column = reader.OpenRowGroup(0).OpenColumn(0); // column 0: null when (row + 0) % 5 == 0
        var values = new int[100];
        var levels = new int[100];

        var read = column.ReadBatch(values, levels);

        await Assert.That(read).IsEqualTo(new BatchRead(100, 80));
        await Assert.That(levels.Count(l => l == 1)).IsEqualTo(80);
        await Assert.That(values[0]).IsEqualTo(1 * 3 - 500); // row 0 is null, so the first value is row 1's
    }

    [Test]
    public async Task ByteArrays_AreViewsIntoThePage_ValidUntilTheNextRead()
    {
        using var reader = ReadHelper.Open(SampleFile.Build(optional: false, rows: 10));
        using var column = reader.OpenRowGroup(0).OpenColumn("text");
        var views = new ReadOnlyMemory<byte>[10];

        var read = column.ReadByteArrays(views);
        var decoded = views.Select(v => System.Text.Encoding.UTF8.GetString(v.Span)).ToArray();

        await Assert.That(read.Rows).IsEqualTo(10);
        await Assert.That(decoded).IsEquivalentTo(Enumerable.Range(0, 10).Select(i => new string('é', i % 4) + i));
    }

    [Test]
    public async Task EmptyAndSingleRowFiles_Work()
    {
        var schema = new ParquetSchema([new PrimitiveNode("n", PhysicalType.Int64, FieldRepetitionType.Optional)]);
        var stream = new MemoryStream();
        using (var writer = new ParquetFileWriter(stream, schema, leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            group.NextColumn().WriteBatch([42L], [1]);
        }

        using var reader = ReadHelper.Open(stream.ToArray());

        await Assert.That(ReadHelper.ReadColumn(reader, 0, 0, 10)).IsEquivalentTo(new object?[] { 42L });

        var empty = new MemoryStream();
        using (new ParquetFileWriter(empty, schema, leaveOpen: true))
        {
        }

        using var emptyReader = ReadHelper.Open(empty.ToArray());
        await Assert.That(emptyReader.RowGroupCount).IsEqualTo(0);
        await Assert.That(emptyReader.RowCount).IsEqualTo(0L);
    }

    [Test]
    public async Task ALargeFile_ReadsBackQuickly_AndCorrectly()
    {
        var schema = new ParquetSchema([
            new PrimitiveNode("id", PhysicalType.Int64, FieldRepetitionType.Required),
            new PrimitiveNode("value", PhysicalType.Double, FieldRepetitionType.Optional),
        ]);
        int rows = 500_000;
        var ids = Enumerable.Range(0, rows).Select(i => (long)i).ToArray();
        var levels = Enumerable.Range(0, rows).Select(i => i % 3 == 0 ? 0 : 1).ToArray();
        var values = Enumerable.Range(0, rows).Where(i => i % 3 != 0).Select(i => i / 7.0).ToArray();
        var stream = new MemoryStream();
        using (var writer = new ParquetFileWriter(stream, schema, leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            group.NextColumn().WriteBatch(ids);
            group.NextColumn().WriteBatch(values, levels);
        }

        using var reader = ReadHelper.Open(stream.ToArray());
        var readIds = new long[rows];
        var readValues = new double[rows];
        var readLevels = new int[rows];
        using (var idColumn = reader.OpenRowGroup(0).OpenColumn(0))
        {
            int done = 0;
            while (done < rows)
            {
                var read = idColumn.ReadBatch(readIds.AsSpan(done, Math.Min(8192, rows - done)));
                done += read.Rows;
            }
        }

        using (var valueColumn = reader.OpenRowGroup(0).OpenColumn(1))
        {
            int rowsDone = 0, valuesDone = 0;
            while (rowsDone < rows)
            {
                int batch = Math.Min(8192, rows - rowsDone);
                var scratch = new double[batch];
                var read = valueColumn.ReadBatch(scratch, readLevels.AsSpan(rowsDone, batch));
                scratch.AsSpan(0, read.Values).CopyTo(readValues.AsSpan(valuesDone));
                rowsDone += read.Rows;
                valuesDone += read.Values;
            }
        }

        await Assert.That(readIds.AsSpan().SequenceEqual(ids)).IsTrue();
        await Assert.That(readLevels.AsSpan().SequenceEqual(levels)).IsTrue();
        await Assert.That(readValues.AsSpan(0, values.Length).SequenceEqual(values)).IsTrue();
    }

    [Test]
    public async Task AnAllNullColumn_IsSplitIntoBoundedPages_AndReadsBack()
    {
        var schema = new ParquetSchema([new PrimitiveNode("n", PhysicalType.Int32, FieldRepetitionType.Optional)]);
        int rows = 2_500_000;
        var stream = new MemoryStream();
        using (var writer = new ParquetFileWriter(stream, schema, leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            group.NextColumn().WriteBatch(ReadOnlySpan<int>.Empty, new int[rows]);
        }

        var bytes = stream.ToArray();
        var file = TestParquetFile.Parse(bytes);
        using var reader = ReadHelper.Open(bytes);
        using var column = reader.OpenRowGroup(0).OpenColumn(0);
        var levels = new int[100_000];
        var values = new int[100_000];
        long total = 0;
        BatchRead read;
        while ((read = column.ReadBatch(values, levels)).Rows > 0)
        {
            total += read.Rows;
            if (read.Values != 0 || levels.AsSpan(0, read.Rows).IndexOfAnyExcept(0) >= 0)
            {
                throw new InvalidOperationException("Expected only nulls.");
            }
        }

        await Assert.That(file.Pages(0, 0).Select(p => p.Header.DataPageHeader!.NumValues))
            .IsEquivalentTo(new[] { 1 << 20, 1 << 20, 2_500_000 - 2 * (1 << 20) });
        await Assert.That(total).IsEqualTo((long)rows);
    }
}
