using ParquetNeo.ColumnTypes;
using ParquetNeo.Core;
using ParquetNeo.Encodings;
using ParquetNeo.Exceptions;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;
using ParquetNeo.Tests.Core;

namespace ParquetNeo.Tests.Logical;

public class Int96Tests
{
    private const int Rows = 40;

    // The values tests/fixtures/generate_int96_fixtures.py writes: ts_ns and ts_us, five values each, repeated.
    private static readonly long[] Nanoseconds = [0, -1, 1_700_000_000_123_456_789, 4_102_444_800_000_000_001, -2_000_000_000_000_000_000];
    private static readonly long[] Microseconds = [0, -62_135_596_800_000_000, 253_402_300_799_999_999, 1_700_000_000_123_456, -1];

    private static long FloorDivide(long dividend, long divisor) => dividend / divisor - (dividend % divisor < 0 ? 1 : 0);

    private static DateTime Expected(int column, int row) => column == 0
        ? new DateTime(DateTime.UnixEpoch.Ticks + FloorDivide(Nanoseconds[row % 5], 100))
        : new DateTime(DateTime.UnixEpoch.Ticks + (Microseconds[row % 5] * 10));

    private static bool IsNull(int column, int row) => (row + column) % 7 == 0;

    [Test]
    [Arguments("pyarrow-int96-required-plain.parquet", false)]
    [Arguments("pyarrow-int96-optional-plain.parquet", true)]
    [Arguments("pyarrow-int96-optional-dictionary.parquet", true)]
    [Arguments("pyarrow-int96-optional-v2-snappy.parquet", true)]
    public async Task Int96Timestamps_ReadAsDateTimes_AndDateTimeOffsets(string fixture, bool optional)
    {
        using var file = ReadHelper.OpenFixture(fixture);
        await Assert.That(file.Schema.Columns.Select(c => c.Type)).IsEquivalentTo(new[] { PhysicalType.Int96, PhysicalType.Int96 });

        for (int column = 0; column < 2; column++)
        {
            foreach (int batch in new[] { 1, 7, 100 })
            {
                using var reader = file.OpenRowGroup(0).OpenColumn(column);
                using var offsetReader = file.OpenRowGroup(0).OpenColumn(column);
                var values = new DateTime[batch];
                var offsets = new DateTimeOffset[batch];
                var levels = optional ? new int[batch] : [];
                var offsetLevels = optional ? new int[batch] : [];
                int row = 0;
                BatchRead read;
                while ((read = reader.ReadBatch(values, levels)).Entries > 0)
                {
                    var offsetRead = offsetReader.ReadBatch(offsets, offsetLevels);
                    await Assert.That(offsetRead).IsEqualTo(read);
                    int next = 0;
                    for (int entry = 0; entry < read.Entries; entry++, row++)
                    {
                        bool isNull = optional && IsNull(column, row);
                        await Assert.That(!optional || levels[entry] == 1).IsEqualTo(!isNull).Because($"column {column} row {row}");
                        if (!isNull)
                        {
                            await Assert.That(values[next]).IsEqualTo(Expected(column, row)).Because($"column {column} row {row}");
                            await Assert.That(values[next].Kind).IsEqualTo(DateTimeKind.Unspecified);
                            await Assert.That(offsets[next]).IsEqualTo(new DateTimeOffset(Expected(column, row), TimeSpan.Zero));
                            next++;
                        }
                    }
                }

                await Assert.That(row).IsEqualTo(Rows).Because($"column {column} batch {batch}");
            }
        }
    }

    [Test]
    public async Task TheExistingPlainFixture_HoldsReadableInt96Timestamps()
    {
        using var file = ReadHelper.OpenFixture("pyarrow-plain-v1.parquet");
        using var reader = file.OpenRowGroup(0).OpenColumn("ts96");
        var values = new DateTime[1000];
        var levels = new int[1000];

        var read = reader.ReadBatch(values, levels);

        int next = 0;
        for (int i = 0; i < 1000; i++)
        {
            if (i % 10 == 0)
            {
                await Assert.That(levels[i]).IsEqualTo(0);
                continue;
            }

            // 2020-01-01 plus i * 3601 s and i microseconds (the Python generator).
            var expected = new DateTime(2020, 1, 1).AddSeconds(i * 3601L).AddTicks(i * 10L);
            await Assert.That(values[next++]).IsEqualTo(expected).Because($"row {i}");
        }

        await Assert.That(read.Values).IsEqualTo(900);
    }

    [Test]
    public async Task Int96Columns_CannotBeReadAsOtherTypes()
    {
        using var file = ReadHelper.OpenFixture("pyarrow-int96-required-plain.parquet");
        var group = file.OpenRowGroup(0);

        await Assert.That(() => group.OpenColumn(0).ReadBatch(new long[1])).Throws<ArgumentException>();
        await Assert.That(() => group.OpenColumn(0).ReadBatch(new decimal[1])).Throws<ArgumentException>();
        await Assert.That(() => group.OpenColumn(0).ReadFixedLengthByteArrays(new byte[12])).Throws<ArgumentException>();
        await Assert.That(() => group.OpenColumn(0).ReadByteArrays(new ReadOnlyMemory<byte>[1])).Throws<ArgumentException>();
    }

    [Test]
    public async Task Int96Columns_CannotBeWritten()
    {
        var schema = new ParquetSchema([new PrimitiveNode("t", PhysicalType.Int96, FieldRepetitionType.Required)]);

        await Assert.That(() => new ParquetFileWriter(new MemoryStream(), schema)).Throws<NotSupportedException>();
    }

    [Test]
    public async Task AnInt96ValueOutsideTheYearsOneToNineThousand_IsReported()
    {
        var plain = File.ReadAllBytes(TestBytes.FixturePath("pyarrow-int96-required-plain.parquet"));
        var page = TestParquetFile.Parse(plain).DataPages(0, 0)[0];
        int start = (int)page.Offset + page.HeaderLength;

        // The first value is 12 bytes: nanoseconds within the day, then the Julian day.
        var dayTooLate = (byte[])plain.Clone();
        BitConverter.GetBytes(3_000_000_000L).CopyTo(dayTooLate, start);
        BitConverter.GetBytes(1_721_426 + 3_652_059).CopyTo(dayTooLate, start + 8);
        var dayTooEarly = (byte[])plain.Clone();
        BitConverter.GetBytes(1_721_425).CopyTo(dayTooEarly, start + 8);
        var nanosecondsPastTheDay = (byte[])plain.Clone();
        BitConverter.GetBytes(86_400_000_000_000L).CopyTo(nanosecondsPastTheDay, start);
        var negativeNanoseconds = (byte[])plain.Clone();
        BitConverter.GetBytes(-1L).CopyTo(negativeNanoseconds, start);
        var lastTick = (byte[])plain.Clone();
        BitConverter.GetBytes(86_399_999_999_999L).CopyTo(lastTick, start);
        BitConverter.GetBytes(1_721_426 + 3_652_058).CopyTo(lastTick, start + 8);

        foreach (var bytes in new[] { dayTooLate, dayTooEarly, nanosecondsPastTheDay, negativeNanoseconds })
        {
            await Assert.That(() => ReadHelper.Open(bytes).OpenRowGroup(0).OpenColumn(0).ReadBatch(new DateTime[1])).Throws<ValueConversionException>();
        }

        var read = new DateTime[1];
        ReadHelper.Open(lastTick).OpenRowGroup(0).OpenColumn(0).ReadBatch(read);
        await Assert.That(read[0]).IsEqualTo(DateTime.MaxValue); // the last nanosecond of the last day rounds down to the last tick
    }

    [Test]
    public async Task TheJulianDayNumbers_MapToTheCalendar()
    {
        // 2440588 is 1970-01-01, 1721426 is 0001-01-01.
        await Assert.That(new DateTime(Int96Timestamp.ToTicks(new Int96(0, 2_440_588)))).IsEqualTo(DateTime.UnixEpoch);
        await Assert.That(new DateTime(Int96Timestamp.ToTicks(new Int96(0, 1_721_426)))).IsEqualTo(DateTime.MinValue);
        await Assert.That(new DateTime(Int96Timestamp.ToTicks(new Int96(43_200_000_000_099, 2_440_588)))).IsEqualTo(DateTime.UnixEpoch.AddHours(12).AddTicks(0)).Because("99 ns is below a tick");
        await Assert.That(new DateTime(Int96Timestamp.ToTicks(new Int96(1_000_000_000, 2_440_589)))).IsEqualTo(DateTime.UnixEpoch.AddDays(1).AddSeconds(1));
    }
}
