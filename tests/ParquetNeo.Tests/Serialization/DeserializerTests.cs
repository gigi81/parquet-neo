using ParquetNeo.DataModel;
using ParquetNeo.Serialization;
using ParquetNeo.Tests.Core;

namespace ParquetNeo.Tests.Serialization;

public class DeserializerTests
{
    private static byte[] Serialize<TRow>(IEnumerable<TRow> rows, ParquetSerializerOptions? options = null)
    {
        var stream = new MemoryStream();
        ParquetSerializer.Serialize(rows, stream, options);
        return stream.ToArray();
    }

    private static ParquetDeserializerOptions WithCodecs => new() { Reader = new ParquetReaderOptions { Codecs = TestParquetFile.Registry } };

    private static async Task AssertSameRows(List<Sample> expected, List<Sample> actual, string where)
    {
        await Assert.That(actual.Count).IsEqualTo(expected.Count).Because(where);
        for (int i = 0; i < expected.Count; i++)
        {
            await Assert.That(RowComparer.Difference(expected[i], actual[i])).IsNull().Because($"{where}, row {i}");
        }
    }

    [Test]
    [Arguments(0, 100)]
    [Arguments(1, 100)]
    [Arguments(250, 100_000)]
    [Arguments(250, 64)]
    [Arguments(2500, 600)]
    public async Task Rows_RoundTrip_ThroughEveryReadPath(int count, int rowGroupSize)
    {
        var rows = Sample.Rows(count);
        var bytes = Serialize(rows, new ParquetSerializerOptions { RowGroupSize = rowGroupSize });
        string where = $"{count} rows in groups of {rowGroupSize}";

        await AssertSameRows(rows, ParquetSerializer.Deserialize<Sample>(new MemoryStream(bytes)), $"{where}, sync");
        await AssertSameRows(rows, ParquetSerializer.DeserializeAll<Sample>(new MemoryStream(bytes)).ToList(), $"{where}, lazy");
        await AssertSameRows(rows, await ParquetSerializer.DeserializeAsync<Sample>(new MemoryStream(bytes)), $"{where}, async");
        var streamed = new List<Sample>();
        await foreach (var row in ParquetSerializer.DeserializeAllAsync<Sample>(new MemoryStream(bytes)))
        {
            streamed.Add(row);
        }

        await AssertSameRows(rows, streamed, $"{where}, async stream");
    }

    [Test]
    public async Task TheSourceStream_StaysOpen()
    {
        var bytes = Serialize(Sample.Rows(10));
        var sync = new MemoryStream(bytes);
        var asynchronous = new MemoryStream(bytes);

        ParquetSerializer.Deserialize<Sample>(sync);
        await ParquetSerializer.DeserializeAsync<Sample>(asynchronous);

        await Assert.That(sync.CanRead).IsTrue();
        await Assert.That(asynchronous.CanRead).IsTrue();
    }

    [Test]
    public async Task AStreamThatCannotSeek_OrReadIsRefused()
    {
        var bytes = Serialize(Sample.Rows(2));

        await Assert.That(() => ParquetSerializer.Deserialize<Sample>(new NonSeekable(bytes))).Throws<ArgumentException>();
        await Assert.That(() => ParquetSerializer.DeserializeAll<Sample>(new NonSeekable(bytes))).Throws<ArgumentException>();
        await Assert.That(async () => await ParquetSerializer.DeserializeAsync<Sample>(new NonSeekable(bytes))).Throws<ArgumentException>();
        await Assert.That(() => ParquetSerializer.DeserializeAllAsync<Sample>(new NonSeekable(bytes))).Throws<ArgumentException>();
        await Assert.That(() => ParquetSerializer.Deserialize<Sample>(null!)).Throws<ArgumentNullException>();
    }

    private sealed class NonSeekable(byte[] bytes) : MemoryStream(bytes)
    {
        public override bool CanSeek => false;
    }

    [Test]
    public async Task ACancelledToken_StopsTheReading()
    {
        var bytes = Serialize(Sample.Rows(10));
        using var source = new CancellationTokenSource();
        await source.CancelAsync();

        await Assert.That(async () => await ParquetSerializer.DeserializeAsync<Sample>(new MemoryStream(bytes), null, source.Token)).Throws<OperationCanceledException>();
    }

    // ---- files written by pyarrow ----

    private sealed class LogicalRow
    {
        [ParquetColumn("int8")]
        public sbyte? Int8 { get; set; }

        [ParquetColumn("int16")]
        public short? Int16 { get; set; }

        [ParquetColumn("uint8")]
        public byte? UInt8 { get; set; }

        [ParquetColumn("uint16")]
        public ushort? UInt16 { get; set; }

        [ParquetColumn("uint32")]
        public uint? UInt32 { get; set; }

        [ParquetColumn("uint64")]
        public ulong? UInt64 { get; set; }

        [ParquetColumn("date")]
        public DateOnly? Date { get; set; }

        [ParquetColumn("time_ms")]
        public TimeOnly? TimeMilliseconds { get; set; }

        [ParquetColumn("time_us")]
        public TimeOnly? TimeMicroseconds { get; set; }

        [ParquetColumn("time_ns")]
        public TimeOnly? TimeNanoseconds { get; set; }

        [ParquetColumn("timestamp_ms")]
        public DateTime? TimestampMilliseconds { get; set; }

        [ParquetColumn("timestamp_us")]
        public DateTimeOffset? TimestampMicroseconds { get; set; }

        [ParquetColumn("timestamp_ns")]
        public DateTime? TimestampNanoseconds { get; set; }

        [ParquetColumn("local_us")]
        public DateTime? Local { get; set; }

        [ParquetColumn("uuid")]
        public Guid? Uuid { get; set; }

        [ParquetColumn("half")]
        public Half? Half { get; set; }
    }

    [Test]
    [Arguments("pyarrow-logical-required-plain.parquet", false)]
    [Arguments("pyarrow-logical-optional-plain.parquet", true)]
    [Arguments("pyarrow-logical-optional-dictionary.parquet", true)]
    [Arguments("pyarrow-logical-optional-v2-snappy.parquet", true)]
    public async Task ThePyArrowLogicalFiles_ReadIntoRows(string fixture, bool optional)
    {
        var bytes = File.ReadAllBytes(TestBytes.FixturePath(fixture));
        var syncRows = ParquetSerializer.Deserialize<LogicalRow>(new MemoryStream(bytes), WithCodecs);
        var asyncRows = await ParquetSerializer.DeserializeAsync<LogicalRow>(new MemoryStream(bytes), WithCodecs);

        await Assert.That(syncRows.Count).IsEqualTo(Logical.LogicalDataset.RowCount);
        foreach (var rows in new[] { syncRows, asyncRows })
        {
            for (int row = 0; row < rows.Count; row++)
            {
                var properties = typeof(LogicalRow).GetProperties();
                for (int column = 0; column < properties.Length; column++)
                {
                    object? expected = optional && Logical.LogicalDataset.IsNull(column, row) ? null : Logical.LogicalDataset.ValueAt(column, row);
                    if (expected is DateTime moment && properties[column].PropertyType == typeof(DateTimeOffset?))
                    {
                        expected = new DateTimeOffset(DateTime.SpecifyKind(moment, DateTimeKind.Utc));
                    }

                    await Assert.That(properties[column].GetValue(rows[row])).IsEqualTo(expected).Because($"{fixture} row {row} {properties[column].Name}");
                }
            }
        }
    }

    private sealed class DecimalTextRow
    {
        [ParquetColumn("decimal_int32")]
        public decimal Decimal32 { get; set; }

        [ParquetColumn("decimal_int64")]
        public decimal? Decimal64 { get; set; }

        [ParquetColumn("decimal_fixed")]
        public decimal? DecimalFixed { get; set; }

        [ParquetColumn("decimal_wide")]
        public decimal? DecimalWide { get; set; }

        [ParquetColumn("decimal_bytes")]
        public decimal? DecimalBytes { get; set; }

        [ParquetColumn("text")]
        public string? Text { get; set; }

        [ParquetColumn("enum")]
        public string? Enum { get; set; }

        [ParquetColumn("json")]
        public string? Json { get; set; }
    }

    [Test]
    public async Task ThePyArrowDecimalAndTextFile_ReadsIntoRows()
    {
        var bytes = File.ReadAllBytes(TestBytes.FixturePath("pyarrow-decimal-required-plain.parquet"));

        var rows = await ParquetSerializer.DeserializeAsync<DecimalTextRow>(new MemoryStream(bytes), WithCodecs);

        await Assert.That(rows.Count).IsEqualTo(Logical.DecimalTextDataset.RowCount);
        for (int row = 0; row < rows.Count; row++)
        {
            await Assert.That(rows[row].Decimal32).IsEqualTo((decimal)Logical.DecimalTextDataset.ValueAt(0, row));
            await Assert.That(rows[row].Decimal64).IsEqualTo((decimal)Logical.DecimalTextDataset.ValueAt(1, row));
            await Assert.That(rows[row].DecimalFixed).IsEqualTo((decimal)Logical.DecimalTextDataset.ValueAt(2, row));
            await Assert.That(rows[row].DecimalWide).IsEqualTo((decimal)Logical.DecimalTextDataset.ValueAt(3, row));
            await Assert.That(rows[row].DecimalBytes).IsEqualTo((decimal)Logical.DecimalTextDataset.ValueAt(4, row));
            await Assert.That(rows[row].Text).IsEqualTo((string)Logical.DecimalTextDataset.ValueAt(5, row));
            await Assert.That(rows[row].Enum).IsEqualTo((string)Logical.DecimalTextDataset.ValueAt(6, row));
            await Assert.That(rows[row].Json).IsEqualTo((string)Logical.DecimalTextDataset.ValueAt(7, row));
        }
    }

    private sealed class Int96Row
    {
        [ParquetColumn("ts_ns")]
        public DateTime? Nanoseconds { get; set; }

        [ParquetColumn("ts_us")]
        public DateTimeOffset? Microseconds { get; set; }
    }

    [Test]
    [Arguments("pyarrow-int96-required-plain.parquet")]
    [Arguments("pyarrow-int96-optional-dictionary.parquet")]
    public async Task Int96Files_ReadIntoRows(string fixture)
    {
        var bytes = File.ReadAllBytes(TestBytes.FixturePath(fixture));

        var rows = await ParquetSerializer.DeserializeAsync<Int96Row>(new MemoryStream(bytes), WithCodecs);

        await Assert.That(rows.Count).IsEqualTo(40);
        await Assert.That(rows.Count(r => r.Nanoseconds is null)).IsEqualTo(fixture.Contains("required") ? 0 : 6);
        await Assert.That(rows[0].Microseconds).IsEqualTo(new DateTimeOffset(DateTime.UnixEpoch));
        await Assert.That(rows[2].Nanoseconds).IsEqualTo(DateTime.UnixEpoch.AddTicks(17_000_000_001_234_567L));
    }
}
