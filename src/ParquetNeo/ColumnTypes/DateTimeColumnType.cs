using System.Buffers;
using ParquetNeo.Core;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.ColumnTypes;

/// <summary>
/// A TIMESTAMP column in any unit as <see cref="DateTime"/>: UTC when the column is adjusted to UTC, otherwise
/// <see cref="DateTimeKind.Unspecified"/>. When writing, a UTC-adjusted column converts local times to UTC and takes unspecified
/// ones as UTC. Precision finer than 100 ns is dropped on reading. The deprecated INT96 timestamp can be read, as a time without a
/// kind, but not written. By default a column is in microseconds and adjusted to UTC; those are its settings.
/// </summary>
internal sealed class DateTimeColumnType(TimeUnit unit = TimeUnit.Micros, bool isAdjustedToUtc = true) : ColumnType<DateTime>
{
    public override ColumnSettingKinds SupportedSettings => ColumnSettingKinds.Timestamp;

    public override ColumnType<DateTime> Configure(ColumnSettings settings) => new DateTimeColumnType(settings.Unit ?? unit, settings.IsAdjustedToUtc ?? isAdjustedToUtc);

    public override PrimitiveNode CreateNode(string name, FieldRepetitionType repetition) =>
        new(name, PhysicalType.Int64, repetition, LogicalType.Timestamp(isAdjustedToUtc, unit));

    public override string? GetProblem(PrimitiveNode column) => TimestampTicks.Problem(column, Name);

    protected override BatchRead ReadCore(ColumnReader reader, Span<DateTime> values, Span<int> definitionLevels, Span<int> repetitionLevels)
    {
        if (reader.Column.Type != PhysicalType.Int96)
        {
            return PhysicalConversion.Read<long, DateTime, Converter>(reader, Converter.Create(reader.Column), values, definitionLevels, repetitionLevels);
        }

        var scratch = ArrayPool<long>.Shared.Rent(Math.Max(values.Length, 1));
        try
        {
            BatchRead read = reader.ReadInt96Ticks(scratch.AsSpan(0, values.Length), definitionLevels, repetitionLevels);
            for (int i = 0; i < read.Values; i++)
            {
                values[i] = new DateTime(scratch[i], DateTimeKind.Unspecified);
            }

            return read;
        }
        finally
        {
            ArrayPool<long>.Shared.Return(scratch);
        }
    }

    protected override void WriteCore(ColumnWriter writer, ReadOnlySpan<DateTime> values, ReadOnlySpan<int> definitionLevels, ReadOnlySpan<int> repetitionLevels) =>
        PhysicalConversion.Write<long, DateTime, Converter>(writer, Converter.Create(writer.Column), values, definitionLevels, repetitionLevels);

    internal readonly struct Converter : IValueConverter<long, DateTime, Converter>
    {
        private readonly TimeUnit _unit;
        private readonly bool _isAdjustedToUtc;

        private Converter(TimeUnit unit, bool isAdjustedToUtc)
        {
            _unit = unit;
            _isAdjustedToUtc = isAdjustedToUtc;
        }

        public static Converter Create(PrimitiveNode column) => new(column.LogicalType!.Unit, column.LogicalType.IsAdjustedToUtc);

        public DateTime ToValue(long physical) =>
            new(TimestampTicks.ToTicks(physical, _unit), _isAdjustedToUtc ? DateTimeKind.Utc : DateTimeKind.Unspecified);

        public long ToPhysical(DateTime value) =>
            TimestampTicks.FromTicks(_isAdjustedToUtc && value.Kind == DateTimeKind.Local ? value.ToUniversalTime().Ticks : value.Ticks, _unit);
    }
}
