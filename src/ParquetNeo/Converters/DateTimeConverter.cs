using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Converters;

/// <summary>
/// Reads and writes a TIMESTAMP column as <see cref="DateTime"/>. A timestamp that is adjusted to UTC reads as
/// <see cref="DateTimeKind.Utc"/> and a local one as <see cref="DateTimeKind.Unspecified"/>; when writing, a UTC-adjusted
/// column converts local times to UTC and takes unspecified ones as UTC. Precision finer than 100 ns is dropped on reading.
/// </summary>
internal readonly struct DateTimeConverter : IValueConverter<long, DateTime, DateTimeConverter>
{
    private readonly TimeUnit _unit;
    private readonly bool _isAdjustedToUtc;

    private DateTimeConverter(TimeUnit unit, bool isAdjustedToUtc)
    {
        _unit = unit;
        _isAdjustedToUtc = isAdjustedToUtc;
    }

    public static DateTimeConverter Create(PrimitiveNode column) =>
        new(TimestampTicks.Unit(column, nameof(DateTime)), column.LogicalType!.IsAdjustedToUtc);

    public DateTime ToValue(long physical) =>
        new(TimestampTicks.ToTicks(physical, _unit), _isAdjustedToUtc ? DateTimeKind.Utc : DateTimeKind.Unspecified);

    public long ToPhysical(DateTime value) =>
        TimestampTicks.FromTicks(_isAdjustedToUtc && value.Kind == DateTimeKind.Local ? value.ToUniversalTime().Ticks : value.Ticks, _unit);
}
