using ParquetNeo.Exceptions;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.ColumnTypes;

/// <summary>The arithmetic shared by the TIMESTAMP converters: counts of milliseconds, microseconds or nanoseconds since 1970 and .NET ticks.</summary>
internal static class TimestampTicks
{
    private static readonly long EpochTicks = DateTime.UnixEpoch.Ticks;

    /// <summary>Why a column cannot be read or written as a moment in time, or null when it can: a TIMESTAMP stored as INT64, or an INT96.</summary>
    public static string? Problem(PrimitiveNode column, string typeName) =>
        (column.Type == PhysicalType.Int64 && column.LogicalType is { Kind: LogicalTypeKind.Timestamp }) || column.Type == PhysicalType.Int96
            ? null
            : ColumnProblems.Mismatch(column, typeName);

    /// <summary>The ticks (of the .NET calendar, from 0001-01-01) of a stored count, rounding toward the past below one tick.</summary>
    /// <exception cref="ValueConversionException">The moment is outside the years 1 to 9999.</exception>
    public static long ToTicks(long physical, TimeUnit unit)
    {
        try
        {
            long sinceEpoch = unit switch
            {
                TimeUnit.Millis => checked(physical * TimeSpan.TicksPerMillisecond),
                TimeUnit.Micros => checked(physical * (TimeSpan.TicksPerMillisecond / 1000)),
                _ => FloorDivide(physical, 100),
            };
            long ticks = checked(EpochTicks + sinceEpoch);
            if (ticks is >= 0 and <= 3_155_378_975_999_999_999)
            {
                return ticks;
            }
        }
        catch (OverflowException)
        {
            // handled below
        }

        throw new ValueConversionException($"The timestamp {physical} is outside the years 1 to 9999 that DateTime holds.");
    }

    /// <exception cref="ArgumentOutOfRangeException">The moment does not fit the stored count.</exception>
    public static long FromTicks(long ticks, TimeUnit unit)
    {
        long sinceEpoch = ticks - EpochTicks;
        try
        {
            return unit switch
            {
                TimeUnit.Millis => FloorDivide(sinceEpoch, TimeSpan.TicksPerMillisecond),
                TimeUnit.Micros => FloorDivide(sinceEpoch, TimeSpan.TicksPerMillisecond / 1000),
                _ => checked(sinceEpoch * 100),
            };
        }
        catch (OverflowException)
        {
            throw new ArgumentOutOfRangeException(nameof(ticks), "The moment does not fit a timestamp in nanoseconds.");
        }
    }

    private static long FloorDivide(long dividend, long divisor)
    {
        long quotient = Math.DivRem(dividend, divisor, out long remainder);
        return remainder < 0 ? quotient - 1 : quotient;
    }
}
