using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Converters;

/// <summary>
/// Reads and writes a TIMESTAMP column as <see cref="DateTimeOffset"/>, always at offset zero: Parquet stores the moment
/// (or a local time) but not an offset. Writing converts the value to UTC.
/// </summary>
internal readonly struct DateTimeOffsetConverter : IValueConverter<long, DateTimeOffset, DateTimeOffsetConverter>
{
    private readonly TimeUnit _unit;

    private DateTimeOffsetConverter(TimeUnit unit)
    {
        _unit = unit;
    }

    public static DateTimeOffsetConverter Create(PrimitiveNode column) => new(TimestampTicks.Unit(column, nameof(DateTimeOffset)));

    public DateTimeOffset ToValue(long physical) => new(TimestampTicks.ToTicks(physical, _unit), TimeSpan.Zero);

    public long ToPhysical(DateTimeOffset value) => TimestampTicks.FromTicks(value.UtcTicks, _unit);
}
