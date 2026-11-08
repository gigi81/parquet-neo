using ParquetNeo.Exceptions;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.ColumnTypes;

/// <summary>A DATE column (INT32 days since 1970-01-01) as <see cref="DateOnly"/>.</summary>
internal sealed class DateColumnType : ConvertedColumnType<DateOnly, int, DateColumnType.Converter>
{
    public override PrimitiveNode CreateNode(string name, FieldRepetitionType repetition) => new(name, PhysicalType.Int32, repetition, LogicalType.Date);

    public override string? GetProblem(PrimitiveNode column) =>
        column.Type == PhysicalType.Int32 && column.LogicalType is { Kind: LogicalTypeKind.Date } ? null : ColumnProblems.Mismatch(column, Name);

    internal readonly struct Converter : IValueConverter<int, DateOnly, Converter>
    {
        private static readonly int EpochDayNumber = new DateOnly(1970, 1, 1).DayNumber;

        public static Converter Create(PrimitiveNode column) => default;

        public DateOnly ToValue(int physical)
        {
            long dayNumber = (long)physical + EpochDayNumber;
            return dayNumber is >= 0 and <= 3_652_058
                ? DateOnly.FromDayNumber((int)dayNumber)
                : throw new ValueConversionException($"The date {physical} days from 1970-01-01 is outside the years 1 to 9999 that DateOnly holds.");
        }

        public int ToPhysical(DateOnly value) => value.DayNumber - EpochDayNumber;
    }
}
