using System.Buffers;
using ParquetNeo.Core;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.ColumnTypes;

/// <summary>
/// A TIMESTAMP column in any unit as <see cref="DateTimeOffset"/>, always at offset zero: Parquet stores the moment (or a local
/// time) but not an offset. Writing converts the value to UTC. The deprecated INT96 timestamp can be read, not written. By default
/// a column is in microseconds and adjusted to UTC; those are its settings.
/// </summary>
internal sealed class DateTimeOffsetColumnType(TimeUnit unit = TimeUnit.Micros, bool isAdjustedToUtc = true) : ColumnType<DateTimeOffset>
{
    public override ColumnSettingKinds SupportedSettings => ColumnSettingKinds.Timestamp;

    public override ColumnType<DateTimeOffset> Configure(ColumnSettings settings) => new DateTimeOffsetColumnType(settings.Unit ?? unit, settings.IsAdjustedToUtc ?? isAdjustedToUtc);

    public override PrimitiveNode CreateNode(string name, FieldRepetitionType repetition) =>
        new(name, PhysicalType.Int64, repetition, LogicalType.Timestamp(isAdjustedToUtc, unit));

    public override string? GetProblem(PrimitiveNode column) => TimestampTicks.Problem(column, Name);

    protected override BatchRead ReadCore(ColumnReader reader, Span<DateTimeOffset> values, Span<int> definitionLevels, Span<int> repetitionLevels)
    {
        if (reader.Column.Type != PhysicalType.Int96)
        {
            return PhysicalConversion.Read<long, DateTimeOffset, Converter>(reader, Converter.Create(reader.Column), values, definitionLevels, repetitionLevels);
        }

        var scratch = ArrayPool<long>.Shared.Rent(Math.Max(values.Length, 1));
        try
        {
            BatchRead read = reader.ReadInt96Ticks(scratch.AsSpan(0, values.Length), definitionLevels, repetitionLevels);
            for (int i = 0; i < read.Values; i++)
            {
                values[i] = new DateTimeOffset(scratch[i], TimeSpan.Zero);
            }

            return read;
        }
        finally
        {
            ArrayPool<long>.Shared.Return(scratch);
        }
    }

    protected override void WriteCore(ColumnWriter writer, ReadOnlySpan<DateTimeOffset> values, ReadOnlySpan<int> definitionLevels, ReadOnlySpan<int> repetitionLevels) =>
        PhysicalConversion.Write<long, DateTimeOffset, Converter>(writer, Converter.Create(writer.Column), values, definitionLevels, repetitionLevels);

    internal readonly struct Converter : IValueConverter<long, DateTimeOffset, Converter>
    {
        private readonly TimeUnit _unit;

        private Converter(TimeUnit unit)
        {
            _unit = unit;
        }

        public static Converter Create(PrimitiveNode column) => new(column.LogicalType!.Unit);

        public DateTimeOffset ToValue(long physical) => new(TimestampTicks.ToTicks(physical, _unit), TimeSpan.Zero);

        public long ToPhysical(DateTimeOffset value) => TimestampTicks.FromTicks(value.UtcTicks, _unit);
    }
}
