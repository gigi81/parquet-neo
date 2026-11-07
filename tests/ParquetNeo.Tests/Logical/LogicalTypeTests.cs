using ParquetNeo.Core;
using ParquetNeo.Exceptions;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;
using ParquetNeo.Tests.Core;

namespace ParquetNeo.Tests.Logical;

public class LogicalTypeTests
{
    private static async Task AssertDataset(byte[] bytes, bool optional, string where)
    {
        using var reader = ReadHelper.Open(bytes);
        for (int column = 0; column < LogicalDataset.Names.Length; column++)
        {
            foreach (int batch in new[] { 1, 7, 100 })
            {
                using var columnReader = reader.OpenRowGroup(0).OpenColumn(column);
                var actual = LogicalDataset.Read(columnReader, column, batch);

                await Assert.That(actual.Length).IsEqualTo(LogicalDataset.RowCount).Because($"{where} {LogicalDataset.Names[column]}");
                for (int row = 0; row < actual.Length; row++)
                {
                    object? expected = optional && LogicalDataset.IsNull(column, row) ? null : LogicalDataset.ValueAt(column, row);
                    await Assert.That(actual[row]).IsEqualTo(expected).Because($"{where} {LogicalDataset.Names[column]} row {row} batch {batch}");
                }
            }
        }
    }

    [Test]
    [Arguments("pyarrow-logical-required-plain.parquet", false)]
    [Arguments("pyarrow-logical-optional-plain.parquet", true)]
    [Arguments("pyarrow-logical-optional-dictionary.parquet", true)]
    [Arguments("pyarrow-logical-optional-v2-snappy.parquet", true)]
    public async Task ThePyArrowLogicalFiles_ReadAsTheirDotNetTypes(string fixture, bool optional)
    {
        await AssertDataset(File.ReadAllBytes(TestBytes.FixturePath(fixture)), optional, fixture);
    }

    [Test]
    public async Task TimestampKinds_FollowTheAdjustmentToUtc()
    {
        using var reader = ReadHelper.OpenFixture("pyarrow-logical-required-plain.parquet");
        var utc = new DateTime[5];
        var local = new DateTime[5];
        var offsets = new DateTimeOffset[5];

        reader.OpenRowGroup(0).OpenColumn("timestamp_us").ReadBatch(utc);
        reader.OpenRowGroup(0).OpenColumn("local_us").ReadBatch(local);
        reader.OpenRowGroup(0).OpenColumn("timestamp_us").ReadBatch(offsets);

        await Assert.That(utc.All(d => d.Kind == DateTimeKind.Utc)).IsTrue();
        await Assert.That(local.All(d => d.Kind == DateTimeKind.Unspecified)).IsTrue();
        await Assert.That(offsets.All(d => d.Offset == TimeSpan.Zero)).IsTrue();
        await Assert.That(offsets.Select(d => d.UtcDateTime)).IsEquivalentTo(utc);
    }

    [Test]
    public async Task ThePhysicalOverloads_StillGiveTheStoredValues()
    {
        using var reader = ReadHelper.OpenFixture("pyarrow-logical-required-plain.parquet");
        var days = new int[5];
        var unsigned = new int[5];
        var micros = new long[5];

        reader.OpenRowGroup(0).OpenColumn("date").ReadBatch(days);
        reader.OpenRowGroup(0).OpenColumn("uint32").ReadBatch(unsigned);
        reader.OpenRowGroup(0).OpenColumn("timestamp_us").ReadBatch(micros);

        await Assert.That(days).IsEquivalentTo(new[] { 0, -719162, 2932896, -1, 19782 });
        await Assert.That(unsigned).IsEquivalentTo(new[] { 0, 1, int.MaxValue, int.MinValue, -1 });
        await Assert.That(micros[3]).IsEqualTo(1_700_000_000_123_456L);
    }

    // ---- our writer ----

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task OurLogicalFiles_RoundTrip_AndCarryTheLogicalTypes(bool optional)
    {
        var bytes = LogicalDataset.Write(optional);

        await AssertDataset(bytes, optional, "round trip");
        var file = TestParquetFile.Parse(bytes);
        file.AssertStructure();
        var expected = LogicalDataset.Schema(optional);
        for (int column = 0; column < LogicalDataset.Names.Length; column++)
        {
            await Assert.That(file.Schema.Columns[column].LogicalType).IsEqualTo(expected.Columns[column].LogicalType);
            await Assert.That(file.Schema.Columns[column].ConvertedType).IsEqualTo(expected.Columns[column].ConvertedType);
        }
    }

    [Test]
    public async Task TheWriterOutput_MatchesTheGoldenLogicalFile_ByteForByte()
    {
        var actual = LogicalDataset.Write(optional: true, new ParquetWriterOptions { CreatedBy = "ParquetNeo golden" });

        string? regenerateTo = Environment.GetEnvironmentVariable("PARQUETNEO_GOLDEN_OUT");
        if (!string.IsNullOrEmpty(regenerateTo))
        {
            File.WriteAllBytes(Path.Combine(regenerateTo, "golden-logical.parquet"), actual);
            return;
        }

        await Assert.That(TestBytes.ToHex(actual)).IsEqualTo(TestBytes.ToHex(File.ReadAllBytes(TestBytes.FixturePath("golden-logical.parquet"))));
    }

    [Test]
    public async Task TheLegacyAnnotations_AreWrittenAlongsideTheLogicalTypes()
    {
        var file = TestParquetFile.Parse(LogicalDataset.Write(optional: false));
        var converted = file.Metadata.Schema.Skip(1).Select(e => e.ConvertedType).ToArray();

        await Assert.That(converted.Take(14)).IsEquivalentTo(new ConvertedType?[]
        {
            ConvertedType.Int8, ConvertedType.Int16, ConvertedType.UInt8, ConvertedType.UInt16, ConvertedType.UInt32, ConvertedType.UInt64,
            ConvertedType.Date, ConvertedType.TimeMillis, ConvertedType.TimeMicros, null, // nanoseconds have no legacy form
            ConvertedType.TimestampMillis, ConvertedType.TimestampMicros, null, ConvertedType.TimestampMicros,
        });
    }

    // ---- values the types cannot hold, in either direction ----

    private static ColumnReader Open(byte[] bytes, int column = 0)
    {
        var reader = ReadHelper.Open(bytes);
        return reader.OpenRowGroup(0).OpenColumn(column);
    }

    private static byte[] WritePhysical(PrimitiveNode node, Action<ColumnWriter> write)
    {
        var stream = new MemoryStream();
        using (var writer = new ParquetFileWriter(stream, new ParquetSchema([node]), leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            write(group.NextColumn());
        }

        return stream.ToArray();
    }

    [Test]
    public async Task StoredValuesOutsideTheirType_AreReported()
    {
        var tooBigForInt8 = WritePhysical(new PrimitiveNode("x", PhysicalType.Int32, FieldRepetitionType.Required, LogicalType.Integer(8, true)), w => w.WriteBatch([200]));
        var tooBigForUInt8 = WritePhysical(new PrimitiveNode("x", PhysicalType.Int32, FieldRepetitionType.Required, LogicalType.Integer(8, false)), w => w.WriteBatch([-1]));
        var tooFarDate = WritePhysical(new PrimitiveNode("x", PhysicalType.Int32, FieldRepetitionType.Required, LogicalType.Date), w => w.WriteBatch([2932897]));
        var tooEarlyDate = WritePhysical(new PrimitiveNode("x", PhysicalType.Int32, FieldRepetitionType.Required, LogicalType.Date), w => w.WriteBatch([-719163]));
        var negativeTime = WritePhysical(new PrimitiveNode("x", PhysicalType.Int32, FieldRepetitionType.Required, LogicalType.Time(false, TimeUnit.Millis)), w => w.WriteBatch([-1]));
        var fullDayMillis = WritePhysical(new PrimitiveNode("x", PhysicalType.Int32, FieldRepetitionType.Required, LogicalType.Time(false, TimeUnit.Millis)), w => w.WriteBatch([86_400_000]));
        var yearTenThousand = WritePhysical(new PrimitiveNode("x", PhysicalType.Int64, FieldRepetitionType.Required, LogicalType.Timestamp(true, TimeUnit.Micros)), w => w.WriteBatch([253_402_300_800_000_000L]));
        var longTime = WritePhysical(new PrimitiveNode("x", PhysicalType.Int64, FieldRepetitionType.Required, LogicalType.Time(false, TimeUnit.Micros)), w => w.WriteBatch([86_400_000_000L]));
        var farTimestamp = WritePhysical(new PrimitiveNode("x", PhysicalType.Int64, FieldRepetitionType.Required, LogicalType.Timestamp(true, TimeUnit.Millis)), w => w.WriteBatch([long.MaxValue]));
        var beforeYearOne = WritePhysical(new PrimitiveNode("x", PhysicalType.Int64, FieldRepetitionType.Required, LogicalType.Timestamp(true, TimeUnit.Micros)), w => w.WriteBatch([-62_135_596_800_000_001L]));

        await Assert.That(() => Open(tooBigForInt8).ReadBatch(new sbyte[1])).Throws<ValueConversionException>();
        await Assert.That(() => Open(tooBigForUInt8).ReadBatch(new byte[1])).Throws<ValueConversionException>();
        await Assert.That(() => Open(tooFarDate).ReadBatch(new DateOnly[1])).Throws<ValueConversionException>();
        await Assert.That(() => Open(tooEarlyDate).ReadBatch(new DateOnly[1])).Throws<ValueConversionException>();
        await Assert.That(() => Open(negativeTime).ReadBatch(new TimeOnly[1])).Throws<ValueConversionException>();
        await Assert.That(() => Open(longTime).ReadBatch(new TimeOnly[1])).Throws<ValueConversionException>();
        await Assert.That(() => Open(fullDayMillis).ReadBatch(new TimeOnly[1])).Throws<ValueConversionException>();
        await Assert.That(() => Open(yearTenThousand).ReadBatch(new DateTime[1])).Throws<ValueConversionException>();
        await Assert.That(() => Open(farTimestamp).ReadBatch(new DateTime[1])).Throws<ValueConversionException>();
        await Assert.That(() => Open(beforeYearOne).ReadBatch(new DateTimeOffset[1])).Throws<ValueConversionException>();
    }

    [Test]
    public async Task ValuesTheColumnCannotStore_AreRejectedWhenWriting()
    {
        var nanoseconds = new PrimitiveNode("t", PhysicalType.Int64, FieldRepetitionType.Required, LogicalType.Timestamp(true, TimeUnit.Nanos));
        var schema = new ParquetSchema([nanoseconds]);
        using var file = new ParquetFileWriter(new MemoryStream(), schema, leaveOpen: true);
        using var group = file.AppendRowGroup();
        var column = group.NextColumn();

        // 2262-04-11 is where 64-bit nanoseconds since 1970 run out.
        await Assert.That(() => column.WriteBatch([new DateTime(2300, 1, 1, 0, 0, 0, DateTimeKind.Utc)])).Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task AColumnOfAnotherLogicalType_IsRejected_ByBothDirections()
    {
        var bytes = LogicalDataset.Write(optional: false);
        using var reader = ReadHelper.Open(bytes);
        var date = reader.OpenRowGroup(0).OpenColumn("date");
        var uint8 = reader.OpenRowGroup(0).OpenColumn("uint8");

        await Assert.That(() => date.ReadBatch(new Guid[1])).Throws<ArgumentException>();
        await Assert.That(() => date.ReadBatch(new DateTime[1])).Throws<ArgumentException>();
        await Assert.That(() => uint8.ReadBatch(new short[1])).Throws<ArgumentException>(); // the right width, the wrong signedness
        await Assert.That(() => uint8.ReadBatch(new ushort[1])).Throws<ArgumentException>();

        var schema = LogicalDataset.Schema(optional: false);
        using var writerFile = new ParquetFileWriter(new MemoryStream(), schema, leaveOpen: true);
        using var group = writerFile.AppendRowGroup();
        var int8 = group.NextColumn();
        await Assert.That(() => int8.WriteBatch([(byte)1])).Throws<ArgumentException>();
        await Assert.That(() => int8.WriteBatch([DateOnly.MinValue])).Throws<ArgumentException>();
    }

    [Test]
    public async Task Timestamps_RoundDownBelowOneTick_AndKeepTheirKinds()
    {
        var node = new PrimitiveNode("t", PhysicalType.Int64, FieldRepetitionType.Required, LogicalType.Timestamp(true, TimeUnit.Nanos));
        var bytes = WritePhysical(node, w => w.WriteBatch([-1L, -100L, -101L, 99L, 100L, 150L]));

        var back = new DateTime[6];
        Open(bytes).ReadBatch(back);

        // The floor of nanoseconds / 100: -1 and -100 are one tick before 1970 (-100 exactly), -101 is two.
        await Assert.That(back.Select(d => d.Ticks - DateTime.UnixEpoch.Ticks)).IsEquivalentTo(new long[] { -1, -1, -2, 0, 1, 1 });
    }

    [Test]
    public async Task WritingDateTimes_ConvertsLocalToUtc_ForAdjustedColumns_AndLeavesLocalColumnsAlone()
    {
        var adjusted = new PrimitiveNode("t", PhysicalType.Int64, FieldRepetitionType.Required, LogicalType.Timestamp(true, TimeUnit.Micros));
        var local = new PrimitiveNode("t", PhysicalType.Int64, FieldRepetitionType.Required, LogicalType.Timestamp(false, TimeUnit.Micros));
        var moment = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Local);
        var plusTwo = new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.FromHours(2));

        var utcColumn = WritePhysical(adjusted, w => w.WriteBatch([moment, DateTime.SpecifyKind(moment, DateTimeKind.Unspecified)]));
        var offsetColumn = WritePhysical(new PrimitiveNode("t", PhysicalType.Int64, FieldRepetitionType.Required, LogicalType.Timestamp(true, TimeUnit.Micros)), w => w.WriteBatch([plusTwo]));
        var localColumn = WritePhysical(local, w => w.WriteBatch([moment]));

        var micros = new long[2];
        Open(utcColumn).ReadBatch(micros);
        var offsetMicros = new long[1];
        Open(offsetColumn).ReadBatch(offsetMicros);
        var localMicros = new long[1];
        Open(localColumn).ReadBatch(localMicros);

        long Micros(DateTime utc) => (utc.Ticks - DateTime.UnixEpoch.Ticks) / 10;
        await Assert.That(micros[0]).IsEqualTo(Micros(moment.ToUniversalTime()));
        await Assert.That(micros[1]).IsEqualTo(Micros(DateTime.SpecifyKind(moment, DateTimeKind.Utc))); // unspecified counts as UTC
        await Assert.That(offsetMicros[0]).IsEqualTo(Micros(new DateTime(2024, 5, 6, 5, 8, 9, DateTimeKind.Utc)));
        await Assert.That(localMicros[0]).IsEqualTo(Micros(DateTime.SpecifyKind(moment, DateTimeKind.Utc))); // a local column keeps the wall clock
    }

    [Test]
    public async Task UuidBytes_AreInNetworkOrder_NotTheMixedOrderOfNetGuids()
    {
        var node = new PrimitiveNode("u", PhysicalType.FixedLenByteArray, FieldRepetitionType.Required, LogicalType.Uuid, typeLength: 16);
        var guid = Guid.Parse("00112233-4455-6677-8899-aabbccddeeff");
        var bytes = WritePhysical(node, w => w.WriteBatch([guid]));

        var raw = new byte[16];
        Open(bytes).ReadFixedLengthByteArrays(raw);
        var back = new Guid[1];
        Open(bytes).ReadBatch(back);

        await Assert.That(Convert.ToHexString(raw)).IsEqualTo("00112233445566778899AABBCCDDEEFF");
        await Assert.That(back[0]).IsEqualTo(guid);
    }

    [Test]
    public async Task Float16_RoundTripsEverySpecialValue()
    {
        var node = new PrimitiveNode("h", PhysicalType.FixedLenByteArray, FieldRepetitionType.Required, LogicalType.Float16, typeLength: 2);
        Half[] values = [Half.Zero, Half.NegativeZero, Half.One, Half.MaxValue, Half.MinValue, Half.Epsilon, Half.PositiveInfinity, Half.NegativeInfinity, Half.NaN];
        var bytes = WritePhysical(node, w => w.WriteBatch(values));

        var back = new Half[values.Length];
        Open(bytes).ReadBatch(back);

        await Assert.That(back.Select(BitConverter.HalfToUInt16Bits)).IsEquivalentTo(values.Select(BitConverter.HalfToUInt16Bits));
    }

    [Test]
    public async Task Levels_WorkWithTheLogicalOverloads_InNestedColumns()
    {
        var node = ParquetNeo.Nested.NestedSchema.List("dates", FieldRepetitionType.Optional, new PrimitiveNode("element", PhysicalType.Int32, FieldRepetitionType.Optional, LogicalType.Date));
        var schema = new ParquetSchema([node]);
        var stream = new MemoryStream();
        DateOnly[] dates = [new(2024, 1, 1), new(2024, 1, 2), new(2025, 3, 4)];
        using (var writer = new ParquetFileWriter(stream, schema, leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            // rows: [d1, null, d2], [], null, [d3]
            group.NextColumn().WriteBatch(dates, [3, 2, 3, 1, 0, 3], [0, 1, 1, 0, 0, 0]);
        }

        using var reader = ReadHelper.Open(stream.ToArray());
        using var column = reader.OpenRowGroup(0).OpenColumn(0);
        var values = new DateOnly[6];
        var definition = new int[6];
        var repetition = new int[6];

        var read = column.ReadBatch(values, definition, repetition);

        await Assert.That(read).IsEqualTo(new BatchRead(6, 3));
        await Assert.That(values.Take(3)).IsEquivalentTo(dates);
        await Assert.That(definition).IsEquivalentTo(new[] { 3, 2, 3, 1, 0, 3 });
        await Assert.That(repetition).IsEquivalentTo(new[] { 0, 1, 1, 0, 0, 0 });
    }

    [Test]
    public async Task AnUnknownLogicalType_FallsBackToThePhysicalType()
    {
        // A UUID is the union member with id 14, written as the header byte 0xEC followed by an empty struct. Renumber it to the
        // unused id 9, which is what a logical type this library has never heard of looks like. A UUID has no legacy annotation
        // to fall back on, so the column is left as a plain FIXED_LEN_BYTE_ARRAY(16).
        var bytes = File.ReadAllBytes(TestBytes.FixturePath("pyarrow-logical-required-plain.parquet"));
        int footerLength = BitConverter.ToInt32(bytes, bytes.Length - 8);
        int footerStart = bytes.Length - 8 - footerLength;
        int marker = footerStart + bytes.AsSpan(footerStart, footerLength).IndexOf(new byte[] { 0xEC, 0x00, 0x00 });
        await Assert.That(marker).IsGreaterThan(footerStart);
        await Assert.That(bytes.AsSpan(marker + 1, footerLength - (marker - footerStart) - 1).IndexOf(new byte[] { 0xEC, 0x00, 0x00 })).IsEqualTo(-1); // the only one
        bytes[marker] = 0x9C;

        using var reader = ReadHelper.Open(bytes);
        var column = reader.Schema.Columns.Single(c => c.Name == "uuid");
        using var uuids = reader.OpenRowGroup(0).OpenColumn("uuid");

        await Assert.That(column.LogicalType).IsNull();
        await Assert.That(column.Type).IsEqualTo(PhysicalType.FixedLenByteArray);
        await Assert.That(() => uuids.ReadBatch(new Guid[1])).Throws<ArgumentException>();

        var raw = new byte[16 * 5];
        var read = uuids.ReadFixedLengthByteArrays(raw);
        await Assert.That(read.Values).IsEqualTo(5);
        await Assert.That(Convert.ToHexString(raw.AsSpan(32, 16))).IsEqualTo("00112233445566778899AABBCCDDEEFF");

        // The rest of the file is untouched by the unknown annotation.
        using var dates = reader.OpenRowGroup(0).OpenColumn("date");
        var days = new DateOnly[5];
        dates.ReadBatch(days);
        await Assert.That(days[1]).IsEqualTo(new DateOnly(1, 1, 1));
    }
}
