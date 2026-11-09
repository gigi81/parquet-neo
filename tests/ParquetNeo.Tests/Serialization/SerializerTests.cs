using ParquetNeo.Core;
using ParquetNeo.Metadata;
using ParquetNeo.Serialization;
using ParquetNeo.Tests.Core;

namespace ParquetNeo.Tests.Serialization;

public class SerializerTests
{
    private static byte[] Serialize<TRow>(IEnumerable<TRow> rows, ParquetSerializerOptions? options = null)
    {
        var stream = new MemoryStream();
        ParquetSerializer.Serialize(rows, stream, options);
        return stream.ToArray();
    }

    private static async Task AssertRoundTrip(byte[] bytes, int rows, string where)
    {
        foreach (string column in Sample.Columns)
        {
            var actual = SerializedColumns.Read(bytes, column);

            await Assert.That(actual.Count).IsEqualTo(rows).Because($"{where} {column}");
            for (int i = 0; i < rows; i++)
            {
                object? expected = Sample.Expected(column, i);
                if (expected is byte[] expectedBytes)
                {
                    await Assert.That(Convert.ToHexString((byte[])actual[i]!)).IsEqualTo(Convert.ToHexString(expectedBytes)).Because($"{where} {column} row {i}");
                }
                else
                {
                    await Assert.That(actual[i]).IsEqualTo(expected).Because($"{where} {column} row {i}");
                }
            }
        }
    }

    // ---- the schema ----

    [Test]
    public async Task TheSchema_IsInferredFromTheProperties()
    {
        var schema = ParquetSerializer.GetSchema<Sample>();
        var columns = schema.Columns.ToDictionary(c => c.Name);

        await Assert.That(schema.Columns.Select(c => c.Name)).IsEquivalentTo(Sample.Columns);
        (string Name, PhysicalType Type, LogicalType? Logical, FieldRepetitionType Repetition, int? Length)[] expected =
        [
            ("Flag", PhysicalType.Boolean, null, FieldRepetitionType.Required, null),
            ("Tiny", PhysicalType.Int32, LogicalType.Integer(8, true), FieldRepetitionType.Required, null),
            ("Byte", PhysicalType.Int32, LogicalType.Integer(8, false), FieldRepetitionType.Required, null),
            ("Short", PhysicalType.Int32, LogicalType.Integer(16, true), FieldRepetitionType.Required, null),
            ("UShort", PhysicalType.Int32, LogicalType.Integer(16, false), FieldRepetitionType.Required, null),
            ("Int", PhysicalType.Int32, null, FieldRepetitionType.Required, null),
            ("UInt", PhysicalType.Int32, LogicalType.Integer(32, false), FieldRepetitionType.Required, null),
            ("Long", PhysicalType.Int64, null, FieldRepetitionType.Required, null),
            ("ULong", PhysicalType.Int64, LogicalType.Integer(64, false), FieldRepetitionType.Required, null),
            ("Float", PhysicalType.Float, null, FieldRepetitionType.Required, null),
            ("Double", PhysicalType.Double, null, FieldRepetitionType.Required, null),
            ("Half", PhysicalType.FixedLenByteArray, LogicalType.Float16, FieldRepetitionType.Required, 2),
            ("Money", PhysicalType.Int64, LogicalType.Decimal(2, 12), FieldRepetitionType.Required, null),
            ("Precise", PhysicalType.FixedLenByteArray, LogicalType.Decimal(18, 38), FieldRepetitionType.Required, 16),
            ("Name", PhysicalType.ByteArray, LogicalType.String, FieldRepetitionType.Required, null),
            ("Data", PhysicalType.ByteArray, null, FieldRepetitionType.Required, null),
            ("Id", PhysicalType.FixedLenByteArray, LogicalType.Uuid, FieldRepetitionType.Required, 16),
            ("When", PhysicalType.Int64, LogicalType.Timestamp(true, TimeUnit.Micros), FieldRepetitionType.Required, null),
            ("Offset", PhysicalType.Int64, LogicalType.Timestamp(true, TimeUnit.Micros), FieldRepetitionType.Required, null),
            ("Day", PhysicalType.Int32, LogicalType.Date, FieldRepetitionType.Required, null),
            ("Time", PhysicalType.Int64, LogicalType.Time(false, TimeUnit.Micros), FieldRepetitionType.Required, null),
            ("Nickname", PhysicalType.ByteArray, LogicalType.String, FieldRepetitionType.Optional, null),
            ("OptionalInt", PhysicalType.Int32, null, FieldRepetitionType.Optional, null),
            ("OptionalDouble", PhysicalType.Double, null, FieldRepetitionType.Optional, null),
            ("OptionalMoney", PhysicalType.Int64, LogicalType.Decimal(2, 12), FieldRepetitionType.Optional, null),
            ("OptionalWhen", PhysicalType.Int64, LogicalType.Timestamp(true, TimeUnit.Micros), FieldRepetitionType.Optional, null),
            ("OptionalId", PhysicalType.FixedLenByteArray, LogicalType.Uuid, FieldRepetitionType.Optional, 16),
        ];

        foreach (var (name, type, logical, repetition, length) in expected)
        {
            var column = columns[name];
            await Assert.That(column.Type).IsEqualTo(type).Because(name);
            await Assert.That(column.LogicalType).IsEqualTo(logical).Because(name);
            await Assert.That(column.Repetition).IsEqualTo(repetition).Because(name);
            await Assert.That(column.TypeLength).IsEqualTo(length).Because(name);
        }
    }

    [Test]
    public async Task EachCallGetsItsOwnSchemaNodes()
    {
        var first = ParquetSerializer.GetSchema<Sample>();
        var second = ParquetSerializer.GetSchema<Sample>();

        await Assert.That(first.Columns[0]).IsNotSameReferenceAs(second.Columns[0]);
        await Assert.That(first.Columns.Count).IsEqualTo(second.Columns.Count);
    }

    // ---- writing rows ----

    [Test]
    [Arguments(1, 100_000)]
    [Arguments(40, 100_000)]
    [Arguments(250, 100)]
    [Arguments(250, 7)]
    [Arguments(2500, 600)]
    [Arguments(2500, 100_000)] // a row group bigger than the 1024 values the buffers start with
    [Arguments(5000, 3000)]
    public async Task Rows_RoundTrip_ThroughRowGroups(int rows, int rowGroupSize)
    {
        var bytes = Serialize(Sample.Rows(rows), new ParquetSerializerOptions { RowGroupSize = rowGroupSize });

        await AssertRoundTrip(bytes, rows, $"{rows} rows in groups of {rowGroupSize}");
        var file = TestParquetFile.Parse(bytes);
        file.AssertStructure();
        await Assert.That(file.Metadata.RowGroups.Count).IsEqualTo((rows + rowGroupSize - 1) / rowGroupSize);
        await Assert.That(file.Metadata.RowGroups.Select(g => g.NumRows)).IsEquivalentTo(
            Enumerable.Range(0, (rows + rowGroupSize - 1) / rowGroupSize).Select(g => (long)Math.Min(rowGroupSize, rows - (g * rowGroupSize))));
    }

    [Test]
    public async Task NoRows_GiveAValidFileWithTheSchemaAndNoRowGroups()
    {
        var bytes = Serialize(Array.Empty<Sample>());

        using var reader = ReadHelper.Open(bytes);
        await Assert.That(reader.RowGroupCount).IsEqualTo(0);
        await Assert.That(reader.Schema.Columns.Select(c => c.Name)).IsEquivalentTo(Sample.Columns);
        TestParquetFile.Parse(bytes).AssertStructure();
    }

    [Test]
    public async Task ARowGroupThatIsExactlyFull_IsNotFollowedByAnEmptyOne()
    {
        var bytes = Serialize(Sample.Rows(30), new ParquetSerializerOptions { RowGroupSize = 10 });

        await Assert.That(TestParquetFile.Parse(bytes).Metadata.RowGroups.Count).IsEqualTo(3);
    }

    [Test]
    public async Task TheSyncAndAsyncPaths_WriteTheSameBytes()
    {
        var options = new ParquetSerializerOptions { RowGroupSize = 64, Writer = new ParquetWriterOptions { CreatedBy = "same" } };
        var rows = Sample.Rows(300);

        var sync = Serialize(rows, options);
        var asyncStream = new MemoryStream();
        await ParquetSerializer.SerializeAsync(rows, asyncStream, options);
        var streamStream = new MemoryStream();
        await ParquetSerializer.SerializeAsync(ToAsync(rows), streamStream, options);

        await Assert.That(Convert.ToHexString(asyncStream.ToArray())).IsEqualTo(Convert.ToHexString(sync));
        await Assert.That(Convert.ToHexString(streamStream.ToArray())).IsEqualTo(Convert.ToHexString(sync));
    }

    private static async IAsyncEnumerable<T> ToAsync<T>(IEnumerable<T> rows)
    {
        foreach (var row in rows)
        {
            await Task.Yield();
            yield return row;
        }
    }

    [Test]
    public async Task ARowGroup_IsWrittenToTheStream_BeforeTheNextRowsAreRead()
    {
        var stream = new MemoryStream();
        var lengthsSeen = new List<long>();

        async IAsyncEnumerable<Sample> Rows()
        {
            for (int i = 0; i < 100; i++)
            {
                lengthsSeen.Add(stream.Length);
                yield return Sample.Row(i);
                await Task.Yield();
            }
        }

        await ParquetSerializer.SerializeAsync(Rows(), stream, new ParquetSerializerOptions { RowGroupSize = 25 });

        // Only the PAR1 marker before the first group is full, then each full group has been written by the time the next row is asked for.
        await Assert.That(lengthsSeen[0]).IsEqualTo(4);
        await Assert.That(lengthsSeen[24]).IsEqualTo(4);
        await Assert.That(lengthsSeen[25]).IsGreaterThan(4);
        await Assert.That(lengthsSeen[50]).IsGreaterThan(lengthsSeen[25]);
        await Assert.That(lengthsSeen[75]).IsGreaterThan(lengthsSeen[50]);
        await Assert.That(stream.Length).IsGreaterThan(lengthsSeen[75]);
    }

    [Test]
    public async Task TheHeader_IsWrittenBeforeTheFirstRowIsRead_ByEveryOverload()
    {
        var syncStream = new MemoryStream();
        var asyncStream = new MemoryStream();
        long syncLength = -1;
        long asyncLength = -1;

        IEnumerable<Sample> Rows(Stream stream, Action<long> record)
        {
            record(stream.Length);
            yield return Sample.Row(0);
        }

        ParquetSerializer.Serialize(Rows(syncStream, l => syncLength = l), syncStream);
        await ParquetSerializer.SerializeAsync(Rows(asyncStream, l => asyncLength = l), asyncStream);

        await Assert.That(syncLength).IsEqualTo(4);
        await Assert.That(asyncLength).IsEqualTo(4);
    }

    [Test]
    public async Task AStreamThatCannotSeek_OrReadIsEnough_AndStaysOpen()
    {
        var inner = new MemoryStream();
        var stream = new WriteOnlyStream(inner);

        await ParquetSerializer.SerializeAsync(Sample.Rows(50), stream);
        ParquetSerializer.Serialize(Sample.Rows(50), stream);

        await Assert.That(stream.CanWrite).IsTrue();
        await Assert.That(stream.Closed).IsFalse();
        var all = inner.ToArray();
        await Assert.That(all.Length % 2).IsEqualTo(0);
        await Assert.That(Convert.ToHexString(all[..(all.Length / 2)])).IsEqualTo(Convert.ToHexString(all[(all.Length / 2)..]));
    }

    private sealed class WriteOnlyStream(Stream inner) : Stream
    {
        public bool Closed { get; private set; }

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => !Closed;

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

        protected override void Dispose(bool disposing)
        {
            Closed = true;
            base.Dispose(disposing);
        }
    }

    [Test]
    public async Task ACancelledToken_StopsTheSerialization()
    {
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.That(async () => await ParquetSerializer.SerializeAsync(Sample.Rows(10), new MemoryStream(), null, source.Token)).Throws<OperationCanceledException>();
        await Assert.That(async () => await ParquetSerializer.SerializeAsync(ToAsync(Sample.Rows(10)), new MemoryStream(), null, source.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task TheSerializerOutput_MatchesTheGoldenFile_ByteForByte()
    {
        var actual = Serialize(Sample.Rows(40), new ParquetSerializerOptions { RowGroupSize = 16, Writer = new ParquetWriterOptions { CreatedBy = "ParquetNeo golden" } });

        string? regenerateTo = Environment.GetEnvironmentVariable("PARQUETNEO_GOLDEN_OUT");
        if (!string.IsNullOrEmpty(regenerateTo))
        {
            File.WriteAllBytes(Path.Combine(regenerateTo, "golden-serializer.parquet"), actual);
            return;
        }

        await Assert.That(TestBytes.ToHex(actual)).IsEqualTo(TestBytes.ToHex(File.ReadAllBytes(TestBytes.FixturePath("golden-serializer.parquet"))));
    }

    // ---- the writer options ----

    [Test]
    public async Task TheWriterOptions_ApplyToTheFile()
    {
        var snappy = new ParquetWriterOptions
        {
            Codecs = TestParquetFile.Registry,
            Compression = CompressionCodec.Snappy,
            DictionaryEnabled = false,
            DataPageVersion = DataPageVersion.V2,
            KeyValueMetadata = new Dictionary<string, string> { ["origin"] = "serializer" },
            CreatedBy = "serializer test",
        };

        var bytes = Serialize(Sample.Rows(120), new ParquetSerializerOptions { Writer = snappy, RowGroupSize = 50 });

        await AssertRoundTrip(bytes, 120, "snappy v2");
        var file = TestParquetFile.Parse(bytes);
        await Assert.That(file.Metadata.RowGroups[0].Columns[0].MetaData!.Codec).IsEqualTo(CompressionCodec.Snappy);
        await Assert.That(file.Metadata.CreatedBy).IsEqualTo("serializer test");
        await Assert.That(file.Metadata.KeyValueMetadata!.Single().Value).IsEqualTo("serializer");
        await Assert.That(file.Pages(0, 0).Any(p => p.Header.Type == PageType.DictionaryPage)).IsFalse();
        await Assert.That(file.Pages(0, 0).All(p => p.Header.Type == PageType.DataPageV2)).IsTrue();
    }

    [Test]
    public async Task ABadOptionsValue_IsRejected()
    {
        await Assert.That(() => Serialize(Sample.Rows(1), new ParquetSerializerOptions { RowGroupSize = 0 })).Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => Serialize(Sample.Rows(1), new ParquetSerializerOptions { Writer = null! })).Throws<ArgumentNullException>();
        await Assert.That(() => ParquetSerializer.Serialize<Sample>(null!, new MemoryStream())).Throws<ArgumentNullException>();
        await Assert.That(() => ParquetSerializer.Serialize(Sample.Rows(1), null!)).Throws<ArgumentNullException>();
        await Assert.That(() => ParquetSerializer.Serialize(Sample.Rows(1), new MemoryStream([], writable: false))).Throws<ArgumentException>();
    }

    // ---- nulls ----

    [Test]
    public async Task ANullRow_IsRejected()
    {
        await Assert.That(() => Serialize(new Sample?[] { Sample.Row(0), null }.Cast<Sample>())).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task ANullInARequiredColumn_IsReported_WithThePropertyAndTheRow()
    {
        var rows = Sample.Rows(5);
        rows[3].Name = null!;

        var exception = Assert.Throws<ArgumentException>(() => Serialize(rows));

        await Assert.That(exception.Message).Contains("Sample.Name");
        await Assert.That(exception.Message).Contains("row 3");
    }

    [Test]
    public async Task ARowNumberInTheMessage_CountsAcrossRowGroups()
    {
        var rows = Sample.Rows(30);
        rows[22].Data = null!;

        var exception = Assert.Throws<ArgumentException>(() => Serialize(rows, new ParquetSerializerOptions { RowGroupSize = 10 }));

        await Assert.That(exception.Message).Contains("row 22");
    }

    [Test]
    public async Task AllNullsInAnOptionalColumn_AreWritten()
    {
        var rows = Sample.Rows(20);
        foreach (var row in rows)
        {
            row.Nickname = null;
            row.OptionalInt = null;
        }

        var bytes = Serialize(rows);

        await Assert.That(SerializedColumns.Read(bytes, "Nickname").All(v => v is null)).IsTrue();
        await Assert.That(SerializedColumns.Read(bytes, "OptionalInt").Count).IsEqualTo(20);
        await Assert.That(TestParquetFile.Parse(bytes).Metadata.RowGroups[0].Columns[22].MetaData!.Statistics!.NullCount).IsEqualTo(20);
    }

    // ---- values the columns cannot hold ----

    [Test]
    public async Task ADecimalThatWouldLoseDigits_IsRejected()
    {
        var rows = Sample.Rows(2);
        rows[1].Money = 1.005m; // the column has two decimals

        await Assert.That(() => Serialize(rows)).Throws<ArgumentOutOfRangeException>();
    }
}
