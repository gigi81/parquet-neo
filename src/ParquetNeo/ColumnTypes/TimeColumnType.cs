using ParquetNeo.Core;
using ParquetNeo.Exceptions;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.ColumnTypes;

/// <summary>
/// A TIME column as <see cref="TimeOnly"/>: milliseconds stored as INT32, or microseconds or nanoseconds stored as INT64.
/// <see cref="TimeOnly"/> counts 100 ns ticks, so nanoseconds below that are dropped on reading and times are cut to the unit when
/// writing. The unit and meaning of the column it creates are its settings; reading and writing follow the column.
/// </summary>
internal sealed class TimeColumnType(TimeUnit unit = TimeUnit.Micros, bool isAdjustedToUtc = false) : ColumnType<TimeOnly>
{
    public override ColumnSettingKinds SupportedSettings => ColumnSettingKinds.Time;

    public override ColumnType<TimeOnly> Configure(ColumnSettings settings) => new TimeColumnType(settings.Unit ?? unit, settings.IsAdjustedToUtc ?? isAdjustedToUtc);

    public override PrimitiveNode CreateNode(string name, FieldRepetitionType repetition) =>
        new(name, unit == TimeUnit.Millis ? PhysicalType.Int32 : PhysicalType.Int64, repetition, LogicalType.Time(isAdjustedToUtc, unit));

    public override string? GetProblem(PrimitiveNode column)
    {
        if (column.LogicalType is not { Kind: LogicalTypeKind.Time } time)
        {
            return ColumnProblems.Mismatch(column, Name);
        }

        var expected = time.Unit == TimeUnit.Millis ? PhysicalType.Int32 : PhysicalType.Int64;
        return column.Type == expected ? null : ColumnProblems.Mismatch(column, Name);
    }

    protected override BatchRead ReadCore(ColumnReader reader, Span<TimeOnly> values, Span<int> definitionLevels, Span<int> repetitionLevels) =>
        reader.Column.Type == PhysicalType.Int32
            ? PhysicalConversion.Read<int, TimeOnly, MillisecondsConverter>(reader, MillisecondsConverter.Create(reader.Column), values, definitionLevels, repetitionLevels)
            : PhysicalConversion.Read<long, TimeOnly, MicrosecondsConverter>(reader, MicrosecondsConverter.Create(reader.Column), values, definitionLevels, repetitionLevels);

    protected override void WriteCore(ColumnWriter writer, ReadOnlySpan<TimeOnly> values, ReadOnlySpan<int> definitionLevels, ReadOnlySpan<int> repetitionLevels)
    {
        if (writer.Column.Type == PhysicalType.Int32)
        {
            PhysicalConversion.Write<int, TimeOnly, MillisecondsConverter>(writer, MillisecondsConverter.Create(writer.Column), values, definitionLevels, repetitionLevels);
        }
        else
        {
            PhysicalConversion.Write<long, TimeOnly, MicrosecondsConverter>(writer, MicrosecondsConverter.Create(writer.Column), values, definitionLevels, repetitionLevels);
        }
    }

    /// <summary>Milliseconds within a day, stored as INT32.</summary>
    internal readonly struct MillisecondsConverter : IValueConverter<int, TimeOnly>
    {
        public static MillisecondsConverter Create(PrimitiveNode column) => default;

        public TimeOnly ToValue(int physical) =>
            physical is >= 0 and < 86_400_000
                ? new TimeOnly(physical * TimeSpan.TicksPerMillisecond)
                : throw new ValueConversionException($"The time {physical} ms is not within a day.");

        public int ToPhysical(TimeOnly value) => (int)(value.Ticks / TimeSpan.TicksPerMillisecond);
    }

    /// <summary>Microseconds or nanoseconds within a day, stored as INT64.</summary>
    internal readonly struct MicrosecondsConverter : IValueConverter<long, TimeOnly>
    {
        private readonly long _ticksPerUnitNumerator;
        private readonly long _unitsPerTick;
        private readonly long _unitsPerDay;

        private MicrosecondsConverter(TimeUnit unit)
        {
            // Microseconds are 10 ticks; a tick is 100 nanoseconds.
            (_ticksPerUnitNumerator, _unitsPerTick) = unit == TimeUnit.Micros ? (10L, 1L) : (1L, 100L);
            _unitsPerDay = TimeSpan.TicksPerDay / _ticksPerUnitNumerator * _unitsPerTick;
        }

        public static MicrosecondsConverter Create(PrimitiveNode column) => new(column.LogicalType!.Unit);

        public TimeOnly ToValue(long physical) =>
            // Checked before converting to ticks, since a huge count of microseconds would overflow on the way.
            physical >= 0 && physical < _unitsPerDay
                ? new TimeOnly(physical / _unitsPerTick * _ticksPerUnitNumerator)
                : throw new ValueConversionException($"The time {physical} is not within a day.");

        public long ToPhysical(TimeOnly value) => value.Ticks / _ticksPerUnitNumerator * _unitsPerTick;
    }
}
