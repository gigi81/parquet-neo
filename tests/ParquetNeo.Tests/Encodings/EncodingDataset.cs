using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Encodings;

/// <summary>
/// The dataset of the encoding fixtures: one column per encoding case, with the formulas that
/// tests/fixtures/encoding_dataset.py describes. Change both together.
/// </summary>
internal static class EncodingDataset
{
    public const int RowCount = 3000;

    public static readonly (string Name, PhysicalType Type, ParquetEncoding Encoding)[] Columns =
    [
        ("delta_int32", PhysicalType.Int32, ParquetEncoding.DeltaBinaryPacked),
        ("delta_int32_wrap", PhysicalType.Int32, ParquetEncoding.DeltaBinaryPacked),
        ("delta_int64", PhysicalType.Int64, ParquetEncoding.DeltaBinaryPacked),
        ("delta_int64_wrap", PhysicalType.Int64, ParquetEncoding.DeltaBinaryPacked),
        ("delta_constant", PhysicalType.Int32, ParquetEncoding.DeltaBinaryPacked),
        ("split_float", PhysicalType.Float, ParquetEncoding.ByteStreamSplit),
        ("split_double", PhysicalType.Double, ParquetEncoding.ByteStreamSplit),
        ("split_int32", PhysicalType.Int32, ParquetEncoding.ByteStreamSplit),
        ("delta_length", PhysicalType.ByteArray, ParquetEncoding.DeltaLengthByteArray),
        ("delta_prefix", PhysicalType.ByteArray, ParquetEncoding.DeltaByteArray),
        ("delta_fixed", PhysicalType.FixedLenByteArray, ParquetEncoding.DeltaByteArray),
        ("split_fixed", PhysicalType.FixedLenByteArray, ParquetEncoding.ByteStreamSplit),
    ];

    /// <summary>In an optional file, column <paramref name="column"/> is null at row <paramref name="row"/> when this is true.</summary>
    public static bool IsNull(int column, int row) => (row + column) % 5 == 0;

    public static object Value(int column, int i) => column switch
    {
        0 => i * 3 - 500 + (i % 7) * (i % 11),
        1 => i % 2 == 0 ? int.MinValue : int.MaxValue,
        2 => i * 1_000_003L - 7 + (i % 13) * (i % 5),
        3 => i % 2 == 0 ? long.MinValue : long.MaxValue,
        4 => 42,
        5 => (float)(i * 0.5 - 3.25),
        6 => i * 0.125 + 1e-3,
        7 => i * 7 - 100,
        8 => System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("é", i % 4)) + i),
        9 => System.Text.Encoding.UTF8.GetBytes($"https://example.com/path/{i / 10}/item{i}"),
        10 => new[] { (byte)(i / 256 % 256), (byte)(i % 256), (byte)1, (byte)2 },
        _ => new[] { (byte)(i % 256), (byte)1, (byte)2, (byte)3 },
    };

    public static ParquetSchema Schema(bool optional) => new(Columns.Select(column => (SchemaNode)new PrimitiveNode(
        column.Name,
        column.Type,
        optional ? FieldRepetitionType.Optional : FieldRepetitionType.Required,
        column.Type == PhysicalType.ByteArray ? LogicalType.String : null,
        typeLength: column.Type == PhysicalType.FixedLenByteArray ? 4 : null)).ToList());

    /// <summary>The encodings the columns are meant to use, as the writer options take them.</summary>
    public static Dictionary<string, ParquetEncoding> Encodings() => Columns.ToDictionary(column => column.Name, column => column.Encoding);

    public static byte[] Write(bool optional, ParquetWriterOptions options, int batchRows = RowCount)
    {
        var stream = new MemoryStream();
        using (var writer = new ParquetFileWriter(stream, Schema(optional), options, leaveOpen: true))
        using (var group = writer.AppendRowGroup())
        {
            for (int column = 0; column < Columns.Length; column++)
            {
                var columnWriter = group.NextColumn();
                for (int start = 0; start < RowCount; start += batchRows)
                {
                    int count = Math.Min(batchRows, RowCount - start);
                    var rows = Enumerable.Range(start, count).ToArray();
                    var present = rows.Where(i => !optional || !IsNull(column, i)).ToArray();
                    int[] levels = optional ? rows.Select(i => IsNull(column, i) ? 0 : 1).ToArray() : default!;
                    switch (Columns[column].Type)
                    {
                        case PhysicalType.Int32: columnWriter.WriteBatch(present.Select(i => (int)Value(column, i)).ToArray(), levels); break;
                        case PhysicalType.Int64: columnWriter.WriteBatch(present.Select(i => (long)Value(column, i)).ToArray(), levels); break;
                        case PhysicalType.Float: columnWriter.WriteBatch(present.Select(i => (float)Value(column, i)).ToArray(), levels); break;
                        case PhysicalType.Double: columnWriter.WriteBatch(present.Select(i => (double)Value(column, i)).ToArray(), levels); break;
                        case PhysicalType.ByteArray: columnWriter.WriteByteArrays(present.Select(i => (ReadOnlyMemory<byte>)(byte[])Value(column, i)).ToArray(), levels); break;
                        default: columnWriter.WriteFixedLengthByteArrays(present.SelectMany(i => (byte[])Value(column, i)).ToArray(), levels); break;
                    }
                }
            }
        }

        return stream.ToArray();
    }
}
