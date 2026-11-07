using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Converters;

/// <summary>Reads and writes a TIME column in milliseconds (INT32) as <see cref="TimeOnly"/>.</summary>
internal readonly struct TimeMillisecondsConverter : IValueConverter<int, TimeOnly, TimeMillisecondsConverter>
{
    public static TimeMillisecondsConverter Create(PrimitiveNode column)
    {
        if (column.Type != PhysicalType.Int32 || column.LogicalType is not { Kind: LogicalTypeKind.Time, Unit: TimeUnit.Millis })
        {
            throw ConversionRules.Mismatch(column, nameof(TimeOnly), "TIME column in milliseconds stored as INT32, or in microseconds or nanoseconds stored as INT64");
        }

        return default;
    }

    public TimeOnly ToValue(int physical) =>
        physical is >= 0 and < 86_400_000
            ? new TimeOnly(physical * TimeSpan.TicksPerMillisecond)
            : throw new ParquetFormatException($"The time {physical} ms is not within a day.");

    public int ToPhysical(TimeOnly value) => (int)(value.Ticks / TimeSpan.TicksPerMillisecond);
}
