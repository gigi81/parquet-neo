using System.Globalization;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Core;

/// <summary>A 13-column flat schema covering every physical type and several logical ones, with deterministic data.</summary>
internal static class SampleFile
{
    public static readonly string[] ColumnNames =
        ["i32", "i64", "f32", "f64", "flag", "text", "blob", "fixed", "day", "ts", "money", "small", "big"];

    public static ParquetSchema Schema(FieldRepetitionType repetition) => new(
    [
        new PrimitiveNode("i32", PhysicalType.Int32, repetition),
        new PrimitiveNode("i64", PhysicalType.Int64, repetition),
        new PrimitiveNode("f32", PhysicalType.Float, repetition),
        new PrimitiveNode("f64", PhysicalType.Double, repetition),
        new PrimitiveNode("flag", PhysicalType.Boolean, repetition),
        new PrimitiveNode("text", PhysicalType.ByteArray, repetition, LogicalType.String),
        new PrimitiveNode("blob", PhysicalType.ByteArray, repetition),
        new PrimitiveNode("fixed", PhysicalType.FixedLenByteArray, repetition, typeLength: 4),
        new PrimitiveNode("day", PhysicalType.Int32, repetition, LogicalType.Date),
        new PrimitiveNode("ts", PhysicalType.Int64, repetition, LogicalType.Timestamp(true, TimeUnit.Micros)),
        new PrimitiveNode("money", PhysicalType.FixedLenByteArray, repetition, LogicalType.Decimal(2, 10), typeLength: 5),
        new PrimitiveNode("small", PhysicalType.Int32, repetition, LogicalType.Integer(8, true)),
        new PrimitiveNode("big", PhysicalType.Int64, repetition, LogicalType.Integer(64, false)),
    ]);

    /// <summary>In an optional file, the value of column <paramref name="column"/> at row <paramref name="row"/> is null every 5th row.</summary>
    public static bool IsNull(int column, int row) => (row + column) % 5 == 0;

    public static object Value(int column, int i) => column switch
    {
        0 => i * 3 - 500,
        1 => i * 1_000_003L - 7,
        2 => (float)(i * 0.5 - 3.25),
        3 => i * 0.125 + 1e-3,
        4 => i % 3 == 0,
        5 => System.Text.Encoding.UTF8.GetBytes(new string('é', i % 4) + i),
        6 => Enumerable.Repeat((byte)(i % 256), i % 5).ToArray(),
        7 => new[] { (byte)(i % 256), (byte)1, (byte)2, (byte)3 },
        8 => 18000 + i,
        9 => 1_600_000_000_000_000L + i * 1_000_000L,
        10 => Decimal5(i * 100L + 25),
        11 => i % 200 - 100,
        _ => unchecked((long)((ulong)i * 4_000_000_000_000_000UL)),
    };

    private static byte[] Decimal5(long value)
    {
        var bytes = new byte[5];
        for (int k = 4; k >= 0; k--)
        {
            bytes[k] = (byte)value;
            value >>= 8;
        }

        return bytes;
    }

    /// <summary>Writes rows [from, from + count) as one row group.</summary>
    public static void WriteGroup(ParquetFileWriter writer, bool optional, int from, int count)
    {
        using var group = writer.AppendRowGroup();
        for (int column = 0; column < ColumnNames.Length; column++)
        {
            var rows = Enumerable.Range(from, count).ToArray();
            var present = rows.Where(i => !optional || !IsNull(column, i)).ToArray();
            int[] levels = optional ? rows.Select(i => IsNull(column, i) ? 0 : 1).ToArray() : default!;
            var w = group.NextColumn();
            switch (column)
            {
                case 0 or 8 or 11: w.WriteBatch(present.Select(i => (int)Value(column, i)).ToArray(), levels); break;
                case 1 or 9 or 12: w.WriteBatch(present.Select(i => (long)Value(column, i)).ToArray(), levels); break;
                case 2: w.WriteBatch(present.Select(i => (float)Value(column, i)).ToArray(), levels); break;
                case 3: w.WriteBatch(present.Select(i => (double)Value(column, i)).ToArray(), levels); break;
                case 4: w.WriteBatch(present.Select(i => (bool)Value(column, i)).ToArray(), levels); break;
                case 5: w.WriteStrings(present.Select(i => System.Text.Encoding.UTF8.GetString((byte[])Value(5, i))).ToArray(), levels); break;
                case 6: w.WriteByteArrays(present.Select(i => (ReadOnlyMemory<byte>)(byte[])Value(6, i)).ToArray(), levels); break;
                default: w.WriteFixedLengthByteArrays(present.SelectMany(i => (byte[])Value(column, i)).ToArray(), levels); break;
            }
        }
    }

    public static byte[] Build(bool optional, int rows, int groups = 1, ParquetWriterOptions? options = null)
    {
        var stream = new MemoryStream();
        using (var writer = new ParquetFileWriter(stream, Schema(optional ? FieldRepetitionType.Optional : FieldRepetitionType.Required), options, leaveOpen: true))
        {
            int per = rows / groups;
            for (int g = 0; g < groups; g++)
            {
                WriteGroup(writer, optional, g * per, g == groups - 1 ? rows - g * per : per);
            }
        }

        return stream.ToArray();
    }

    /// <summary>Describes the first difference between a decoded column and the sample data, or null when they match.</summary>
    public static string? FirstDifference(TestParquetFile file, bool optional, int rowGroup, int from, int count, int column) =>
        FirstDifference(file.ReadColumn(rowGroup, column), optional, from, count, column);

    public static string? FirstDifference(object?[] actual, bool optional, int from, int count, int column)
    {
        if (actual.Length != count)
        {
            return $"column {ColumnNames[column]} has {actual.Length} rows, expected {count}";
        }

        for (int k = 0; k < count; k++)
        {
            string expected = optional && IsNull(column, from + k) ? "null" : Canonical(Value(column, from + k));
            if (Canonical(actual[k]) != expected)
            {
                return $"column {ColumnNames[column]} row {from + k}: got {Canonical(actual[k])}, expected {expected}";
            }
        }

        return null;
    }

    public static string Canonical(object? value) => value switch
    {
        null => "null",
        byte[] bytes => Convert.ToHexString(bytes),
        float f => BitConverter.SingleToInt32Bits(f).ToString(CultureInfo.InvariantCulture),
        double d => BitConverter.DoubleToInt64Bits(d).ToString(CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)!,
    };

    /// <summary>The text two values of a column are compared by: text as the canonical form of its UTF-8 bytes.</summary>
    public static string Describe(object value) => value switch
    {
        string text => Canonical(System.Text.Encoding.UTF8.GetBytes(text)),
        _ => Canonical(value),
    };
}
