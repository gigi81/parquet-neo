namespace ParquetNeo.Filtering;

/// <summary>
/// Dates, times and timestamps, as the ticks (100 nanoseconds) the .NET types count in, or as days for a date: a count of milliseconds,
/// microseconds or nanoseconds in the statistics is turned into ticks as the reader turns it into a value, which keeps the order.
/// </summary>
internal sealed class TicksDomain(TicksKind kind, TimeUnit unit) : ValueDomain
{
    private const long TicksPerMillisecond = 10_000;
    private const long TicksPerMicrosecond = 10;
    private const long NanosecondsPerTick = 100;

    private static readonly int EpochDayNumber = DateOnly.FromDateTime(DateTime.UnixEpoch).DayNumber;

    public override object? Normalize(object statistic)
    {
        long count;
        switch (statistic)
        {
            case int value:
                count = value;
                break;
            case long value:
                count = value;
                break;
            default:
                return null;
        }

        try
        {
            return kind switch
            {
                TicksKind.Date => count,
                TicksKind.Time => ToTicks(count),
                _ => checked(DateTime.UnixEpoch.Ticks + ToTicks(count)),
            };
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    public override object ConvertValue(object value, IReadOnlyList<string> path, out bool ordered)
    {
        ordered = true;
        switch (kind)
        {
            case TicksKind.Date:
                return value switch
                {
                    DateOnly date => (long)(date.DayNumber - EpochDayNumber),
                    DateTime moment => (long)(DateOnly.FromDateTime(moment).DayNumber - EpochDayNumber),
                    _ => throw Mismatch(value, path, "dates"),
                };
            case TicksKind.Time:
                return value is TimeOnly time ? time.Ticks : throw Mismatch(value, path, "times of day");
            default:
                return value switch
                {
                    DateTime moment => moment.Ticks,
                    DateTimeOffset moment => moment.UtcTicks,
                    _ => throw Mismatch(value, path, "timestamps"),
                };
        }
    }

    public override int Compare(object left, object right) => ((long)left).CompareTo((long)right);

    private long ToTicks(long count) => unit switch
    {
        TimeUnit.Millis => checked(count * TicksPerMillisecond),
        TimeUnit.Micros => checked(count * TicksPerMicrosecond),
        _ => FloorDivide(count, NanosecondsPerTick),
    };

    private static long FloorDivide(long dividend, long divisor)
    {
        long quotient = Math.DivRem(dividend, divisor, out long remainder);
        return remainder < 0 ? quotient - 1 : quotient;
    }
}
