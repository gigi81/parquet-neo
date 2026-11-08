using ParquetNeo.Metadata;

namespace ParquetNeo.DataModel;

/// <summary>The unit and meaning of the column of a <see cref="TimeOnly"/> property. Without it times are written in microseconds, as wall-clock readings.</summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class ParquetTimeAttribute : Attribute
{
    /// <param name="unit">Milliseconds (stored as INT32), microseconds or nanoseconds (INT64); times are cut to the unit.</param>
    public ParquetTimeAttribute(TimeUnit unit = TimeUnit.Micros)
    {
        if (!Enum.IsDefined(unit))
        {
            throw new ArgumentOutOfRangeException(nameof(unit));
        }

        Unit = unit;
    }

    public TimeUnit Unit { get; }

    /// <summary>Whether the times are in UTC. Defaults to false: a reading of the clock on the wall.</summary>
    public bool IsAdjustedToUtc { get; set; }
}
