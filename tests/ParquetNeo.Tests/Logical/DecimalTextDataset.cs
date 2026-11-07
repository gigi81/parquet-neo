using ParquetNeo.Core;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Logical;

/// <summary>
/// The decimal and text dataset: the columns and five values each that tests/fixtures/decimal_text_dataset.py describes, so
/// that files written by pyarrow can be compared with it. Row <c>i</c> of a column holds value <c>i % 5</c>; in an optional
/// file it is null when <c>(i + column) % 7 == 0</c>. Change both together.
/// </summary>
internal static class DecimalTextDataset
{
    public const int RowCount = 40;

    public static readonly string[] Names = ["decimal_int32", "decimal_int64", "decimal_fixed", "decimal_wide", "decimal_bytes", "text", "enum", "json"];

    public static bool IsNull(int column, int row) => (row + column) % 7 == 0;

    /// <summary>The schema our writer uses: the last decimal is stored as BYTE_ARRAY, which pyarrow cannot write.</summary>
    public static ParquetSchema Schema(bool optional)
    {
        var repetition = optional ? FieldRepetitionType.Optional : FieldRepetitionType.Required;
        return new ParquetSchema(
        [
            new PrimitiveNode("decimal_int32", PhysicalType.Int32, repetition, LogicalType.Decimal(2, 9)),
            new PrimitiveNode("decimal_int64", PhysicalType.Int64, repetition, LogicalType.Decimal(4, 18)),
            new PrimitiveNode("decimal_fixed", PhysicalType.FixedLenByteArray, repetition, LogicalType.Decimal(6, 28), typeLength: 12),
            new PrimitiveNode("decimal_wide", PhysicalType.FixedLenByteArray, repetition, LogicalType.Decimal(10, 40), typeLength: 17),
            new PrimitiveNode("decimal_bytes", PhysicalType.ByteArray, repetition, LogicalType.Decimal(3, 20)),
            new PrimitiveNode("text", PhysicalType.ByteArray, repetition, LogicalType.String),
            new PrimitiveNode("enum", PhysicalType.ByteArray, repetition, LogicalType.Enum),
            new PrimitiveNode("json", PhysicalType.ByteArray, repetition, LogicalType.Json),
        ]);
    }

    public static object[] Values(int column) => column switch
    {
        0 => new object[] { 0.00m, -0.01m, 1234567.89m, -9999999.99m, 42.50m },
        1 => new object[] { 0.0000m, -0.0001m, 12345678901234.5678m, -99999999999999.9999m, 3.1416m },
        2 => new object[] { 0.000000m, -0.000001m, 1234567890123456789012.345678m, -9999999999999999999999.999999m, 2.718282m },
        3 => new object[] { 0.0000000000m, -1.5000000000m, 123456789012345678.1234567890m, -123456789012345678.0000000001m, 0.0000000007m },
        4 => new object[] { 0.000m, -0.001m, 99999999999999999.999m, -99999999999999999.999m, 1000.500m },
        5 => new object[] { "", "a", "héllo wörld", "日本語", "\U0001F600 emoji" },
        6 => new object[] { "RED", "GREEN", "BLUE", "RED", "GREEN" },
        _ => new object[] { "{\"a\":1}", "null", "[]", "\"x\"", "{\"é\":\"日本\"}" },
    };

    public static object ValueAt(int column, int row) => Values(column)[row % 5];

    /// <summary>Reads a whole column through the overload that matches its type, as boxed values (null for a null).</summary>
    public static object?[] Read(ColumnReader reader, int column, int batchSize)
    {
        var result = new List<object?>();
        var levels = reader.Column.MaxDefinitionLevel > 0 ? new int[batchSize] : [];
        while (true)
        {
            BatchRead read;
            Func<int, object> value;
            if (column < 5)
            {
                var v = new decimal[batchSize];
                read = reader.ReadBatch(v, levels);
                value = i => v[i];
            }
            else
            {
                var v = new string[batchSize];
                read = reader.ReadStrings(v, levels);
                value = i => v[i];
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

    /// <summary>Writes the whole dataset as one row group.</summary>
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
                if (column < 5)
                {
                    w.WriteBatch(present.Cast<decimal>().ToArray(), levels);
                }
                else
                {
                    w.WriteStrings(present.Cast<string>().ToArray(), levels);
                }
            }
        }

        return stream.ToArray();
    }
}
