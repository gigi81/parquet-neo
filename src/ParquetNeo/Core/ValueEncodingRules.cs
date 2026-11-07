using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Core;

/// <summary>Which value encodings a column may be written with, and which one <see cref="ParquetWriterOptions.ColumnEncodings"/> picked.</summary>
internal static class ValueEncodingRules
{
    /// <summary>Whether <paramref name="encoding"/> can be chosen for the values of a column of this type (dictionary encoding is a separate switch).</summary>
    public static bool IsAllowed(PrimitiveNode column, ParquetEncoding encoding) => encoding switch
    {
        ParquetEncoding.Plain => true,
        ParquetEncoding.DeltaBinaryPacked => column.Type is PhysicalType.Int32 or PhysicalType.Int64,
        ParquetEncoding.DeltaLengthByteArray => column.Type == PhysicalType.ByteArray,
        ParquetEncoding.DeltaByteArray => column.Type is PhysicalType.ByteArray or PhysicalType.FixedLenByteArray,
        ParquetEncoding.ByteStreamSplit => column.Type is PhysicalType.Int32 or PhysicalType.Int64 or PhysicalType.Float or PhysicalType.Double or PhysicalType.FixedLenByteArray,
        _ => false,
    };

    /// <summary>Checks every entry of <paramref name="columnEncodings"/> against the schema.</summary>
    /// <exception cref="ArgumentException">An entry names a column that does not exist, or an encoding the column cannot use.</exception>
    public static void Validate(ParquetSchema schema, IReadOnlyDictionary<string, ParquetEncoding>? columnEncodings)
    {
        if (columnEncodings is null)
        {
            return;
        }

        foreach (var (path, encoding) in columnEncodings)
        {
            var column = schema.Columns.FirstOrDefault(c => string.Join('.', c.Path) == path)
                ?? throw new ArgumentException($"ColumnEncodings names '{path}', which is not a column of the schema.");
            if (!IsAllowed(column, encoding))
            {
                throw new ArgumentException($"A {column.Type} column ('{path}') cannot be written with the {encoding} encoding.");
            }
        }
    }

    public static ParquetEncoding For(PrimitiveNode column, IReadOnlyDictionary<string, ParquetEncoding>? columnEncodings) =>
        columnEncodings is not null && columnEncodings.TryGetValue(string.Join('.', column.Path), out var encoding) ? encoding : ParquetEncoding.Plain;
}
