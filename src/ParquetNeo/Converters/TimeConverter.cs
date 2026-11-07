using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Converters;

/// <summary>
/// Reads and writes a TIME column in microseconds or nanoseconds (INT64) as <see cref="TimeOnly"/>, which counts 100 ns
/// ticks: nanoseconds below that are dropped on reading.
/// </summary>
internal readonly struct TimeConverter : IValueConverter<long, TimeOnly, TimeConverter>
{
    private readonly long _ticksPerUnitNumerator;
    private readonly long _unitsPerTick;

    private TimeConverter(TimeUnit unit)
    {
        // Microseconds are 10 ticks; a tick is 100 nanoseconds.
        (_ticksPerUnitNumerator, _unitsPerTick) = unit == TimeUnit.Micros ? (10L, 1L) : (1L, 100L);
    }

    public static TimeConverter Create(PrimitiveNode column)
    {
        if (column.Type != PhysicalType.Int64 || column.LogicalType is not { Kind: LogicalTypeKind.Time, Unit: TimeUnit.Micros or TimeUnit.Nanos } time)
        {
            throw ConversionRules.Mismatch(column, nameof(TimeOnly), "TIME column in microseconds or nanoseconds stored as INT64, or in milliseconds stored as INT32");
        }

        return new TimeConverter(time.Unit);
    }

    public TimeOnly ToValue(long physical)
    {
        long ticks = physical / _unitsPerTick * _ticksPerUnitNumerator;
        return physical >= 0 && ticks < TimeSpan.TicksPerDay
            ? new TimeOnly(ticks)
            : throw new ParquetFormatException($"The time {physical} is not within a day.");
    }

    public long ToPhysical(TimeOnly value) => value.Ticks / _ticksPerUnitNumerator * _unitsPerTick;
}
