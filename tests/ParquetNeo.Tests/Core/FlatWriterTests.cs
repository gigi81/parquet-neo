using System.IO.Compression;
using ParquetNeo.Compression;
using ParquetNeo.Core;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Core;

public class FlatWriterTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task AllColumnTypes_RoundTrip(bool optional)
    {
        var bytes = SampleFile.Build(optional, rows: 1000);

        var file = TestParquetFile.Parse(bytes);
        file.AssertStructure();

        for (int column = 0; column < SampleFile.ColumnNames.Length; column++)
        {
            await Assert.That(SampleFile.FirstDifference(file, optional, 0, 0, 1000, column)).IsNull();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SmallPages_SplitEveryColumnIntoManyPages_WithoutLosingValues(bool optional)
    {
        var bytes = SampleFile.Build(optional, rows: 3000, options: new ParquetWriterOptions { DataPageSize = 100 });

        var file = TestParquetFile.Parse(bytes);
        file.AssertStructure();

        for (int column = 0; column < SampleFile.ColumnNames.Length; column++)
        {
            // A boolean takes 1 bit per value, so even a 100-byte page holds hundreds of them.
            int expectedAtLeast = SampleFile.ColumnNames[column] == "flag" ? 2 : 5;
            await Assert.That(file.Pages(0, column).Count).IsGreaterThan(expectedAtLeast).Because(SampleFile.ColumnNames[column]);
            await Assert.That(SampleFile.FirstDifference(file, optional, 0, 0, 3000, column)).IsNull();
        }
    }

    [Test]
    public async Task DefaultPageSize_KeepsASmallColumnInOnePage()
    {
        var file = TestParquetFile.Parse(SampleFile.Build(optional: true, rows: 500));

        for (int column = 0; column < SampleFile.ColumnNames.Length; column++)
        {
            await Assert.That(file.DataPages(0, column).Count).IsEqualTo(1).Because(SampleFile.ColumnNames[column]);
        }
    }

    [Test]
    public async Task PagesStayNearTheTargetSize()
    {
        var bytes = SampleFile.Build(optional: false, rows: 20_000, options: new ParquetWriterOptions { DataPageSize = 4096 });
        var file = TestParquetFile.Parse(bytes);

        // Column 1 is a required INT64: 8 bytes per value, so 512 values fill a 4096-byte page.
        var pages = file.DataPages(0, 1);

        await Assert.That(pages.Take(pages.Count - 1).All(p => p.Header.DataPageHeader!.NumValues == 512)).IsTrue();
        await Assert.That(pages.Count).IsEqualTo((20_000 + 511) / 512);
    }

    [Test]
    public async Task ManyBatches_GiveTheSameFileAsOneBatch()
    {
        // With one big page the page boundaries cannot depend on how the data was split into calls.
        var schema = new ParquetSchema([
            new PrimitiveNode("n", PhysicalType.Int64, FieldRepetitionType.Optional),
            new PrimitiveNode("s", PhysicalType.ByteArray, FieldRepetitionType.Optional, LogicalType.String),
        ]);
        int rows = 5000;
        var levels = Enumerable.Range(0, rows).Select(i => i % 4 == 0 ? 0 : 1).ToArray();
        var numbers = Enumerable.Range(0, rows).Where(i => i % 4 != 0).Select(i => (long)i).ToArray();
        var strings = numbers.Select(n => "v" + n).ToArray();

        byte[] Write(Action<ColumnWriter, ColumnWriter> fill)
        {
            var stream = new MemoryStream();
            using (var writer = new ParquetFileWriter(stream, schema, new ParquetWriterOptions { CreatedBy = "test" }, leaveOpen: true))
            using (var group = writer.AppendRowGroup())
            {
                var first = group.NextColumn();
                fill(first, null!);
                // the second column is written afterwards, see below
                var second = group.NextColumn();
                fill(null!, second);
            }

            return stream.ToArray();
        }

        var whole = Write((n, s) =>
        {
            n?.WriteBatch(numbers, levels);
            s?.WriteStrings(strings, levels);
        });
        var pieces = Write((n, s) =>
        {
            var random = new Random(3);
            int row = 0, value = 0;
            while (row < rows)
            {
                int take = Math.Min(rows - row, random.Next(1, 700));
                var slice = levels.AsSpan(row, take);
                int present = slice.ToArray().Count(l => l == 1);
                n?.WriteBatch(numbers.AsSpan(value, present), slice);
                s?.WriteStrings(strings.AsSpan(value, present), slice);
                row += take;
                value += present;
            }
        });

        await Assert.That(pieces.AsSpan().SequenceEqual(whole)).IsTrue();
    }

    [Test]
    public async Task OptionalColumn_AllNulls_AndNoNulls()
    {
        var schema = new ParquetSchema([
            new PrimitiveNode("allNull", PhysicalType.Int32, FieldRepetitionType.Optional),
            new PrimitiveNode("noNull", PhysicalType.Int32, FieldRepetitionType.Optional),
        ]);
        var stream = new MemoryStream();
        using (var writer = new ParquetFileWriter(stream, schema, leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            group.NextColumn().WriteBatch(ReadOnlySpan<int>.Empty, new int[100]);
            group.NextColumn().WriteBatch(Enumerable.Range(0, 100).ToArray(), Enumerable.Repeat(1, 100).ToArray());
        }

        var file = TestParquetFile.Parse(stream.ToArray());
        file.AssertStructure();

        await Assert.That(file.ReadColumn<int>(0, 0).All(v => v is null)).IsTrue();
        await Assert.That(file.ReadColumn<int>(0, 1).Select(v => v!.Value)).IsEquivalentTo(Enumerable.Range(0, 100));
    }

    [Test]
    public async Task MultipleRowGroups_HaveTheirOwnRowsOrdinalsAndOffsets()
    {
        var bytes = SampleFile.Build(optional: true, rows: 3001, groups: 3, options: new ParquetWriterOptions { DataPageSize = 2000 });

        var file = TestParquetFile.Parse(bytes);
        file.AssertStructure();

        await Assert.That(file.Metadata.RowGroups.Select(g => g.NumRows)).IsEquivalentTo(new long[] { 1000, 1000, 1001 });
        await Assert.That(file.Metadata.NumRows).IsEqualTo(3001L);
        for (int g = 0; g < 3; g++)
        {
            int from = g * 1000;
            int count = g == 2 ? 1001 : 1000;
            for (int column = 0; column < SampleFile.ColumnNames.Length; column++)
            {
                await Assert.That(SampleFile.FirstDifference(file, true, g, from, count, column)).IsNull();
            }
        }
    }

    [Test]
    public async Task Footer_DescribesTheFile()
    {
        var options = new ParquetWriterOptions
        {
            CreatedBy = "unit tests",
            KeyValueMetadata = new Dictionary<string, string> { ["k1"] = "v1", ["k2"] = "v2" },
        };
        var schema = SampleFile.Schema(FieldRepetitionType.Optional);
        var bytes = SampleFile.Build(optional: true, rows: 50, options: options);

        var file = TestParquetFile.Parse(bytes);

        await Assert.That(file.Metadata.Version).IsEqualTo(1);
        await Assert.That(file.Metadata.CreatedBy).IsEqualTo("unit tests");
        await Assert.That(file.Metadata.KeyValueMetadata!.Select(kv => $"{kv.Key}={kv.Value}")).IsEquivalentTo(new[] { "k1=v1", "k2=v2" });
        await Assert.That(file.Metadata.ColumnOrders!.All(o => o == ColumnOrder.TypeDefined)).IsTrue();
        await Assert.That(file.Metadata.ColumnOrders!.Count).IsEqualTo(13);
        await Assert.That(file.Metadata.Schema.Select(TestBytes.ToHex)).IsEquivalentTo(schema.ToElements().Select(TestBytes.ToHex));
    }

    [Test]
    public async Task DefaultCreatedBy_NamesTheLibraryAndItsVersion()
    {
        var file = TestParquetFile.Parse(SampleFile.Build(optional: false, rows: 5));

        await Assert.That(file.Metadata.CreatedBy!).StartsWith("ParquetNeo version ");
        await Assert.That(file.Metadata.KeyValueMetadata).IsNull();
    }

    [Test]
    public async Task LogicalTypes_ReachTheFooterSchema_WithTheirLegacyAnnotations()
    {
        var file = TestParquetFile.Parse(SampleFile.Build(optional: false, rows: 5));

        var text = file.Schema.FindColumn(["text"])!;
        var money = file.Schema.FindColumn(["money"])!;
        var ts = file.Schema.FindColumn(["ts"])!;

        await Assert.That(text.LogicalType).IsEqualTo(LogicalType.String);
        await Assert.That(text.ConvertedType).IsEqualTo(ConvertedType.Utf8);
        await Assert.That(money.LogicalType).IsEqualTo(LogicalType.Decimal(2, 10));
        await Assert.That(ts.ConvertedType).IsEqualTo(ConvertedType.TimestampMicros);
        await Assert.That(file.Schema.FindColumn(["big"])!.LogicalType).IsEqualTo(LogicalType.Integer(64, false));
    }

    [Test]
    public async Task EveryCodec_RoundTrips_AndShrinksCompressibleData()
    {
        var codecs = new[]
        {
            CompressionCodec.Gzip, CompressionCodec.Brotli, CompressionCodec.Snappy, CompressionCodec.Zstd, CompressionCodec.Lz4Raw,
        };
        var plain = SampleFile.Build(optional: true, rows: 4000, groups: 2, options: new ParquetWriterOptions { DataPageSize = 6000 });

        foreach (var codec in codecs)
        {
            var options = new ParquetWriterOptions { Compression = codec, Codecs = TestParquetFile.Registry, DataPageSize = 6000 };
            var bytes = SampleFile.Build(optional: true, rows: 4000, groups: 2, options: options);

            var file = TestParquetFile.Parse(bytes);
            file.AssertStructure();

            await Assert.That(bytes.Length).IsLessThan(plain.Length).Because(codec.ToString());
            for (int g = 0; g < 2; g++)
            {
                for (int column = 0; column < SampleFile.ColumnNames.Length; column++)
                {
                    await Assert.That(file.Chunk(g, column).Codec).IsEqualTo(codec);
                    await Assert.That(SampleFile.FirstDifference(file, true, g, g * 2000, 2000, column)).IsNull().Because($"{codec}/{column}");
                }
            }
        }
    }

    [Test]
    public async Task Compression_ReducesTheCompressedSizeBelowTheUncompressedSize()
    {
        var options = new ParquetWriterOptions { Compression = CompressionCodec.Gzip };
        var file = TestParquetFile.Parse(SampleFile.Build(optional: false, rows: 5000, options: options));

        var chunk = file.Chunk(0, 0);

        await Assert.That(chunk.TotalCompressedSize).IsLessThan(chunk.TotalUncompressedSize);
        await Assert.That(file.Metadata.RowGroups[0].TotalCompressedSize!.Value).IsLessThan(file.Metadata.RowGroups[0].TotalByteSize);
    }

    [Test]
    public async Task GzipLevel_CanBeChosenThroughTheRegistry()
    {
        var registry = new CodecRegistry().Register(new GzipCodec(CompressionLevel.SmallestSize));
        var options = new ParquetWriterOptions { Compression = CompressionCodec.Gzip, Codecs = registry };

        var file = TestParquetFile.Parse(SampleFile.Build(optional: true, rows: 500, options: options));

        await Assert.That(SampleFile.FirstDifference(file, true, 0, 0, 500, 5)).IsNull();
    }

    [Test]
    public async Task TheSameInput_GivesTheSameBytes()
    {
        var options = new ParquetWriterOptions { CreatedBy = "fixed" };

        var first = SampleFile.Build(optional: true, rows: 2000, groups: 2, options: options);
        var second = SampleFile.Build(optional: true, rows: 2000, groups: 2, options: options);

        await Assert.That(first.AsSpan().SequenceEqual(second)).IsTrue();
    }

    [Test]
    public async Task ANonSeekableStream_ProducesTheSameBytes()
    {
        var reference = SampleFile.Build(optional: true, rows: 1500, groups: 2);
        var target = new MemoryStream();
        using (var forwardOnly = new ForwardOnlyStream(target))
        using (var writer = new ParquetFileWriter(forwardOnly, SampleFile.Schema(FieldRepetitionType.Optional), leaveOpen: true))
        {
            SampleFile.WriteGroup(writer, true, 0, 750);
            SampleFile.WriteGroup(writer, true, 750, 750);
        }

        await Assert.That(target.ToArray().AsSpan().SequenceEqual(reference)).IsTrue();
    }

    [Test]
    public async Task LeaveOpen_DecidesWhetherTheStreamIsClosed()
    {
        var schema = new ParquetSchema([new PrimitiveNode("n", PhysicalType.Int32, FieldRepetitionType.Required)]);

        var kept = new TrackingStream();
        using (var writer = new ParquetFileWriter(kept, schema, leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            group.NextColumn().WriteBatch([1, 2, 3]);
        }

        var closed = new TrackingStream();
        using (var writer = new ParquetFileWriter(closed, schema))
        using (var group = writer.AppendRowGroup())
        {
            group.NextColumn().WriteBatch([1, 2, 3]);
        }

        await Assert.That(kept.IsDisposed).IsFalse();
        await Assert.That(closed.IsDisposed).IsTrue();
    }

    [Test]
    public async Task Close_IsIdempotent_AndDisposeAfterCloseIsHarmless()
    {
        var schema = new ParquetSchema([new PrimitiveNode("n", PhysicalType.Int32, FieldRepetitionType.Required)]);
        var stream = new MemoryStream();
        var writer = new ParquetFileWriter(stream, schema, leaveOpen: true);
        using (var group = writer.AppendRowGroup())
        {
            group.NextColumn().WriteBatch([1, 2, 3]);
        }

        writer.Close();
        long length = stream.Length;
        writer.Close();
        writer.Dispose();

        await Assert.That(stream.Length).IsEqualTo(length);
        TestParquetFile.Parse(stream.ToArray()).AssertStructure();
    }

    [Test]
    public async Task DisposeAsync_WritesTheFooter()
    {
        var schema = new ParquetSchema([new PrimitiveNode("n", PhysicalType.Int64, FieldRepetitionType.Required)]);
        var stream = new MemoryStream();

        await using (var writer = new ParquetFileWriter(stream, schema, leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            group.NextColumn().WriteBatch([10L, 20L, 30L]);
        }

        var file = TestParquetFile.Parse(stream.ToArray());
        await Assert.That(file.ReadColumn<long>(0, 0).Select(v => v!.Value)).IsEquivalentTo(new long[] { 10, 20, 30 });
    }

    [Test]
    public async Task AFileWithNoRowGroups_IsValid()
    {
        var schema = new ParquetSchema([new PrimitiveNode("n", PhysicalType.Int32, FieldRepetitionType.Optional)]);
        var stream = new MemoryStream();
        using (new ParquetFileWriter(stream, schema, leaveOpen: true))
        {
        }

        var file = TestParquetFile.Parse(stream.ToArray());

        await Assert.That(file.Metadata.RowGroups.Count).IsEqualTo(0);
        await Assert.That(file.Metadata.NumRows).IsEqualTo(0L);
        await Assert.That(file.Schema.Columns.Count).IsEqualTo(1);
    }

    [Test]
    public async Task ALargeFile_RoundTrips()
    {
        var schema = new ParquetSchema([
            new PrimitiveNode("id", PhysicalType.Int64, FieldRepetitionType.Required),
            new PrimitiveNode("value", PhysicalType.Double, FieldRepetitionType.Optional),
        ]);
        int rows = 400_000;
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

        var file = TestParquetFile.Parse(stream.ToArray());
        file.AssertStructure();
        var readIds = file.ReadColumn<long>(0, 0);
        var readValues = file.ReadColumn<double>(0, 1);

        await Assert.That(file.Pages(0, 0).Count).IsGreaterThan(2); // 3.2 MB of ids at the 1 MiB default page size
        await Assert.That(readIds.Select(v => v!.Value).SequenceEqual(ids)).IsTrue();
        await Assert.That(readValues.Where(v => v.HasValue).Select(v => v!.Value).SequenceEqual(values)).IsTrue();
        await Assert.That(readValues.Count(v => v is null)).IsEqualTo(levels.Count(l => l == 0));
    }

    /// <summary>A write-only stream that cannot report or change its position.</summary>
    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
    }

    private sealed class TrackingStream : MemoryStream
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
