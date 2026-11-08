using ParquetNeo.Metadata;

namespace ParquetNeo.DataModel;

/// <summary>
/// The unit and meaning of the column of a <see cref="DateTime"/> or <see cref="DateTimeOffset"/> property. Without it
/// timestamps are written in microseconds, as moments in UTC.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class ParquetTimestampAttribute : Attribute
{
    /// <param name="unit">Milliseconds, microseconds or nanoseconds (nanoseconds only reach the years 1677 to 2262).</param>
    public ParquetTimestampAttribute(TimeUnit unit = TimeUnit.Micros)
    {
        if (!Enum.IsDefined(unit))
        {
            throw new ArgumentOutOfRangeException(nameof(unit));
        }

        Unit = unit;
    }

    public TimeUnit Unit { get; }

    /// <summary>
    /// Whether the values are moments in UTC (the default), converting local times to UTC, or wall-clock readings without a
    /// time zone that are stored as they are.
    /// </summary>
    public bool IsAdjustedToUtc { get; set; } = true;
}
