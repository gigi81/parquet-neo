using ParquetNeo.Encodings;
using ParquetNeo.Exceptions;

namespace ParquetNeo.Converters;

/// <summary>
/// Reads the deprecated INT96 timestamp that Impala, Hive and older Spark versions write: the nanoseconds within the day
/// followed by the Julian day number. It carries no time zone; the library reads it as the same clock reading, without a kind.
/// </summary>
internal static class Int96Timestamp
{
    /// <summary>The Julian day number of 0001-01-01, the first day a <see cref="DateTime"/> holds.</summary>
    private const int FirstJulianDay = 1_721_426;

    private const long NanosecondsPerDay = 86_400_000_000_000;

    private const long LastDayOffset = 3_652_058;

    /// <summary>The .NET ticks of the moment, rounding nanoseconds below 100 toward the past.</summary>
    /// <exception cref="ValueConversionException">The moment is outside the years 1 to 9999 or the nanoseconds are not within a day.</exception>
    public static long ToTicks(Int96 value)
    {
        long nanoseconds = value.NanosOfDay;
        long days = (long)value.JulianDay - FirstJulianDay;
        if ((ulong)nanoseconds >= NanosecondsPerDay || (ulong)days > LastDayOffset)
        {
            throw new ValueConversionException($"The INT96 timestamp of day {value.JulianDay} and {nanoseconds} ns is not within the years 1 to 9999 that DateTime holds.");
        }

        return (days * TimeSpan.TicksPerDay) + (nanoseconds / 100);
    }
}
