using ParquetNeo.Core;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Logical;

/// <summary>
/// The logical type dataset: the columns and five values each that tests/fixtures/logical_dataset.py describes, so that files
/// written by pyarrow can be compared with it. Row <c>i</c> of a column holds value <c>i % 5</c>; in an optional file it is
/// null when <c>(i + column) % 7 == 0</c>. Change both together.
/// </summary>
internal static class LogicalDataset
{
    public const int RowCount = 40;

    public static readonly string[] Names =
    [
        "int8", "int16", "uint8", "uint16", "uint32", "uint64", "date", "time_ms", "time_us", "time_ns",
        "timestamp_ms", "timestamp_us", "timestamp_ns", "local_us", "uuid", "half",
    ];

    private static readonly string[] Uuids =
    [
        "00000000-0000-0000-0000-000000000000", "ffffffff-ffff-ffff-ffff-ffffffffffff", "00112233-4455-6677-8899-aabbccddeeff",
        "123e4567-e89b-12d3-a456-426614174000", "f81d4fae-7dec-11d0-a765-00a0c91e6bf6",
    ];

    public static bool IsNull(int column, int row) => (row + column) % 7 == 0;

    public static ParquetSchema Schema(bool optional)
    {
        var repetition = optional ? FieldRepetitionType.Optional : FieldRepetitionType.Required;
        return new ParquetSchema(
        [
            new PrimitiveNode("int8", PhysicalType.Int32, repetition, LogicalType.Integer(8, true)),
            new PrimitiveNode("int16", PhysicalType.Int32, repetition, LogicalType.Integer(16, true)),
            new PrimitiveNode("uint8", PhysicalType.Int32, repetition, LogicalType.Integer(8, false)),
            new PrimitiveNode("uint16", PhysicalType.Int32, repetition, LogicalType.Integer(16, false)),
            new PrimitiveNode("uint32", PhysicalType.Int32, repetition, LogicalType.Integer(32, false)),
            new PrimitiveNode("uint64", PhysicalType.Int64, repetition, LogicalType.Integer(64, false)),
            new PrimitiveNode("date", PhysicalType.Int32, repetition, LogicalType.Date),
            new PrimitiveNode("time_ms", PhysicalType.Int32, repetition, LogicalType.Time(false, TimeUnit.Millis)),
            new PrimitiveNode("time_us", PhysicalType.Int64, repetition, LogicalType.Time(false, TimeUnit.Micros)),
            new PrimitiveNode("time_ns", PhysicalType.Int64, repetition, LogicalType.Time(false, TimeUnit.Nanos)),
            new PrimitiveNode("timestamp_ms", PhysicalType.Int64, repetition, LogicalType.Timestamp(true, TimeUnit.Millis)),
            new PrimitiveNode("timestamp_us", PhysicalType.Int64, repetition, LogicalType.Timestamp(true, TimeUnit.Micros)),
            new PrimitiveNode("timestamp_ns", PhysicalType.Int64, repetition, LogicalType.Timestamp(true, TimeUnit.Nanos)),
            new PrimitiveNode("local_us", PhysicalType.Int64, repetition, LogicalType.Timestamp(false, TimeUnit.Micros)),
            new PrimitiveNode("uuid", PhysicalType.FixedLenByteArray, repetition, LogicalType.Uuid, typeLength: 16),
            new PrimitiveNode("half", PhysicalType.FixedLenByteArray, repetition, LogicalType.Float16, typeLength: 2),
        ]);
    }

    /// <summary>The five values of a column as .NET values, worked out here with plain .NET calendar arithmetic.</summary>
    public static object[] Values(int column) => column switch
    {
        0 => new object[] { (sbyte)-128, (sbyte)-1, (sbyte)0, (sbyte)1, (sbyte)127 },
        1 => new object[] { short.MinValue, (short)-1, (short)0, (short)1, short.MaxValue },
        2 => new object[] { (byte)0, (byte)1, (byte)127, (byte)128, (byte)255 },
        3 => new object[] { (ushort)0, (ushort)1, (ushort)32767, (ushort)32768, ushort.MaxValue },
        4 => new object[] { 0u, 1u, 2147483647u, 2147483648u, uint.MaxValue },
        5 => new object[] { 0UL, 1UL, (ulong)long.MaxValue, 1UL << 63, ulong.MaxValue },
        6 => new object[] { new DateOnly(1970, 1, 1), new DateOnly(1, 1, 1), new DateOnly(9999, 12, 31), new DateOnly(1969, 12, 31), new DateOnly(2024, 2, 29) },
        7 => new object[] { new TimeOnly(0), TimeOnly.FromTimeSpan(TimeSpan.FromMilliseconds(1)), new TimeOnly(23, 59, 59, 999), new TimeOnly(12, 0), TimeOnly.FromTimeSpan(TimeSpan.FromMilliseconds(12_345)) },
        8 => new object[] { new TimeOnly(0), new TimeOnly(10), new TimeOnly(863_999_999_990L), new TimeOnly(432_000_000_000L), new TimeOnly(1_234_567_890) },
        9 => new object[] { new TimeOnly(0), new TimeOnly(1), new TimeOnly(863_999_999_999L), new TimeOnly(432_000_000_000L), new TimeOnly(1_234_567_890) },
        10 => new object[] { Utc(0), Utc(-10_000), Utc(10_000), Utc(1_700_000_000_123L * 10_000), Utc(-DateTime.UnixEpoch.Ticks) },
        11 => new object[] { Utc(0), Utc(-10), Utc(10), Utc(1_700_000_000_123_456L * 10), Utc(253_402_300_799_999_999L * 10) },
        12 => new object[] { Utc(0), Utc(-1), Utc(1), Utc(17_000_000_001_234_567L), Utc(-50_000_000_000_000_000L) },
        13 => new object[] { Local(0), Local(-10), Local(10), Local(1_700_000_000_123_456L * 10), Local(-62_135_596_800_000_000L * 10) },
        14 => Uuids.Select(text => (object)Guid.Parse(text)).ToArray(),
        _ => new object[] { Half.Zero, Half.NegativeZero, Half.One, Half.MaxValue, Half.NegativeInfinity },
    };

    /// <summary>The five stored integers of a column, as listed in the Python file.</summary>
    public static object ValueAt(int column, int row) => Values(column)[row % 5];

    private static DateTime Utc(long ticksSinceEpoch) => DateTime.SpecifyKind(DateTime.UnixEpoch.AddTicks(ticksSinceEpoch), DateTimeKind.Utc);

    private static DateTime Local(long ticksSinceEpoch) => DateTime.SpecifyKind(DateTime.UnixEpoch.AddTicks(ticksSinceEpoch), DateTimeKind.Unspecified);

    /// <summary>Reads a whole column through the logical overload that matches its type, as boxed values (null for a null).</summary>
    public static object?[] Read(ColumnReader reader, int column, int batchSize)
    {
        var result = new List<object?>();
        var levels = reader.Column.MaxDefinitionLevel > 0 ? new int[batchSize] : [];
        while (true)
        {
            BatchRead read;
            Func<int, object> value;
            switch (column)
            {
                case 0: { var v = new sbyte[batchSize]; read = reader.ReadBatch(v, levels); value = i => v[i]; break; }
                case 1: { var v = new short[batchSize]; read = reader.ReadBatch(v, levels); value = i => v[i]; break; }
                case 2: { var v = new byte[batchSize]; read = reader.ReadBatch(v, levels); value = i => v[i]; break; }
                case 3: { var v = new ushort[batchSize]; read = reader.ReadBatch(v, levels); value = i => v[i]; break; }
                case 4: { var v = new uint[batchSize]; read = reader.ReadBatch(v, levels); value = i => v[i]; break; }
                case 5: { var v = new ulong[batchSize]; read = reader.ReadBatch(v, levels); value = i => v[i]; break; }
                case 6: { var v = new DateOnly[batchSize]; read = reader.ReadBatch(v, levels); value = i => v[i]; break; }
                case >= 7 and <= 9: { var v = new TimeOnly[batchSize]; read = reader.ReadBatch(v, levels); value = i => v[i]; break; }
                case >= 10 and <= 13: { var v = new DateTime[batchSize]; read = reader.ReadBatch(v, levels); value = i => v[i]; break; }
                case 14: { var v = new Guid[batchSize]; read = reader.ReadBatch(v, levels); value = i => v[i]; break; }
                default: { var v = new Half[batchSize]; read = reader.ReadBatch(v, levels); value = i => v[i]; break; }
            }

            if (read.Entries == 0)
            {
                return result.ToArray();
            }

            int next = 0;
            for (int entry = 0; entry < read.Entries; entry++)
            {
                result.Add(levels.Length > 0 && levels[entry] == 0 ? null : value(next++));
            }
        }
    }

    /// <summary>Writes the whole dataset as one row group using the logical overloads.</summary>
    public static byte[] Write(bool optional, ParquetWriterOptions? options = null)
    {
        var stream = new MemoryStream();
        using (var writer = new ParquetFileWriter(stream, Schema(optional), options, leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            for (int column = 0; column < Names.Length; column++)
            {
                var rows = Enumerable.Range(0, RowCount).ToArray();
                var present = rows.Where(i => !optional || !IsNull(column, i)).Select(i => ValueAt(column, i)).ToArray();
                int[] levels = optional ? rows.Select(i => IsNull(column, i) ? 0 : 1).ToArray() : default!;
                var w = group.NextColumn();
                switch (column)
                {
                    case 0: w.WriteBatch(present.Cast<sbyte>().ToArray(), levels); break;
                    case 1: w.WriteBatch(present.Cast<short>().ToArray(), levels); break;
                    case 2: w.WriteBatch(present.Cast<byte>().ToArray(), levels); break;
                    case 3: w.WriteBatch(present.Cast<ushort>().ToArray(), levels); break;
                    case 4: w.WriteBatch(present.Cast<uint>().ToArray(), levels); break;
                    case 5: w.WriteBatch(present.Cast<ulong>().ToArray(), levels); break;
                    case 6: w.WriteBatch(present.Cast<DateOnly>().ToArray(), levels); break;
                    case >= 7 and <= 9: w.WriteBatch(present.Cast<TimeOnly>().ToArray(), levels); break;
                    case >= 10 and <= 13: w.WriteBatch(present.Cast<DateTime>().ToArray(), levels); break;
                    case 14: w.WriteBatch(present.Cast<Guid>().ToArray(), levels); break;
                    default: w.WriteBatch(present.Cast<Half>().ToArray(), levels); break;
                }
            }
        }

        return stream.ToArray();
    }
}
