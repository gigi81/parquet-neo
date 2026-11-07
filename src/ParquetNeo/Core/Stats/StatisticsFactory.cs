using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Core.Stats;

/// <summary>Chooses how to collect statistics for a column from its physical and logical type.</summary>
internal static class StatisticsFactory
{
    /// <summary>Returns the collector for <paramref name="column"/>, or null when statistics are turned off.</summary>
    public static StatisticsCollector? Create(PrimitiveNode column, ParquetWriterOptions options)
    {
        if (!options.StatisticsEnabled)
        {
            return null;
        }

        var logical = column.LogicalType;
        var kind = logical?.Kind;
        bool unsigned = kind == LogicalTypeKind.Integer && !logical!.IsSigned;
        int truncate = options.StatisticsTruncateLength;

        return column.Type switch
        {
            PhysicalType.Boolean => new BooleanStatistics(),
            PhysicalType.Int32 when unsigned => new ScalarStatistics<int, uint>(),
            PhysicalType.Int32 => new ScalarStatistics<int, int>(),
            PhysicalType.Int64 when unsigned => new ScalarStatistics<long, ulong>(),
            PhysicalType.Int64 => new ScalarStatistics<long, long>(),
            PhysicalType.Float => new ScalarStatistics<float, float>(),
            PhysicalType.Double => new ScalarStatistics<double, double>(),
            PhysicalType.ByteArray or PhysicalType.FixedLenByteArray => kind switch
            {
                null or LogicalTypeKind.Uuid or LogicalTypeKind.Bson => new BinaryStatistics(BinaryOrder.UnsignedBytes, utf8: false, truncate),
                LogicalTypeKind.String or LogicalTypeKind.Enum or LogicalTypeKind.Json => new BinaryStatistics(BinaryOrder.UnsignedBytes, utf8: true, truncate),
                LogicalTypeKind.Decimal => new BinaryStatistics(BinaryOrder.SignedBigEndian, utf8: false, truncate),
                _ => new NullCountStatistics(), // FLOAT16 (compares by value) and types without a defined order
            },
            _ => new NullCountStatistics(),
        };
    }
}
