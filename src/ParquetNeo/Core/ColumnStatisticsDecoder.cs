using System.Buffers.Binary;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Core;

/// <summary>Turns the raw <see cref="Statistics"/> of a column chunk into a <see cref="ColumnStatistics"/>.</summary>
internal static class ColumnStatisticsDecoder
{
    /// <summary>Returns null when the chunk has no statistics.</summary>
    /// <exception cref="ParquetFormatException">A recorded minimum or maximum does not have the size its type requires.</exception>
    public static ColumnStatistics? Decode(PrimitiveNode column, ColumnChunk chunk, IReadOnlyList<ColumnOrder>? columnOrders)
    {
        var raw = chunk.MetaData?.Statistics;
        if (raw is null)
        {
            return null;
        }

        // The deprecated min/max fields are ignored: they were written with an ordering that is wrong for many types.
        // min_value/max_value are only meaningful for a column order this library understands.
        bool orderKnown = columnOrders is not null && column.ColumnIndex < columnOrders.Count && IsSupported(column.Type, columnOrders[column.ColumnIndex]);
        object? min = orderKnown && raw.MinValue is not null ? DecodeValue(column, raw.MinValue, "minimum") : null;
        object? max = orderKnown && raw.MaxValue is not null ? DecodeValue(column, raw.MaxValue, "maximum") : null;

        // A NaN is not a bound: older writers put one there when the data held NaNs, so the bound is dropped.
        if (min is float.NaN or double.NaN)
        {
            min = null;
        }

        if (max is float.NaN or double.NaN)
        {
            max = null;
        }

        return new ColumnStatistics(min, max, raw.NullCount, raw.NanCount, min is null ? null : raw.IsMinValueExact, max is null ? null : raw.IsMaxValueExact);
    }

    private static bool IsSupported(PhysicalType type, ColumnOrder order) =>
        order == ColumnOrder.TypeDefined || (order == ColumnOrder.Ieee754TotalOrder && type is PhysicalType.Float or PhysicalType.Double);

    private static object DecodeValue(PrimitiveNode column, byte[] bytes, string what)
    {
        switch (column.Type)
        {
            case PhysicalType.Boolean:
                Require(bytes, 1, column, what);
                return bytes[0] != 0;
            case PhysicalType.Int32:
                Require(bytes, sizeof(int), column, what);
                return BinaryPrimitives.ReadInt32LittleEndian(bytes);
            case PhysicalType.Int64:
                Require(bytes, sizeof(long), column, what);
                return BinaryPrimitives.ReadInt64LittleEndian(bytes);
            case PhysicalType.Float:
                Require(bytes, sizeof(float), column, what);
                return BinaryPrimitives.ReadSingleLittleEndian(bytes);
            case PhysicalType.Double:
                Require(bytes, sizeof(double), column, what);
                return BinaryPrimitives.ReadDoubleLittleEndian(bytes);
            case PhysicalType.FixedLenByteArray:
                Require(bytes, column.TypeLength ?? 0, column, what);
                return bytes;
            default:
                return bytes;
        }
    }

    private static void Require(byte[] bytes, int length, PrimitiveNode column, string what)
    {
        if (bytes.Length != length)
        {
            throw new ParquetFormatException($"The {what} in the statistics of column '{string.Join('.', column.Path)}' has {bytes.Length} bytes, but a {column.Type} value has {length}.");
        }
    }
}
