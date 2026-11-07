using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Converters;

/// <summary>Reads and writes a DATE column (INT32 days since 1970-01-01) as <see cref="DateOnly"/>.</summary>
internal readonly struct DateConverter : IValueConverter<int, DateOnly, DateConverter>
{
    private static readonly int EpochDayNumber = new DateOnly(1970, 1, 1).DayNumber;

    public static DateConverter Create(PrimitiveNode column)
    {
        ConversionRules.Require(column, PhysicalType.Int32, LogicalTypeKind.Date, nameof(DateOnly), "DATE column stored as INT32");
        return default;
    }

    public DateOnly ToValue(int physical)
    {
        long dayNumber = (long)physical + EpochDayNumber;
        return dayNumber is >= 0 and <= 3_652_058
            ? DateOnly.FromDayNumber((int)dayNumber)
            : throw new ParquetFormatException($"The date {physical} days from 1970-01-01 is outside the years 1 to 9999 that DateOnly holds.");
    }

    public int ToPhysical(DateOnly value) => value.DayNumber - EpochDayNumber;
}
