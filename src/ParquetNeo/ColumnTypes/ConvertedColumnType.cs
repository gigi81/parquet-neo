using ParquetNeo.Core;
using ParquetNeo.Schema;

namespace ParquetNeo.ColumnTypes;

/// <summary>A column type whose values are converted from and to the INT32 or INT64 of the column one by one by a struct converter.</summary>
internal abstract class ConvertedColumnType<TValue, TPhysical, TConverter> : ColumnType<TValue>
    where TPhysical : unmanaged
    where TConverter : struct, IValueConverter<TPhysical, TValue, TConverter>
{
    protected sealed override BatchRead ReadCore(ColumnReader reader, Span<TValue> values, Span<int> definitionLevels, Span<int> repetitionLevels) =>
        PhysicalConversion.Read<TPhysical, TValue, TConverter>(reader, TConverter.Create(reader.Column), values, definitionLevels, repetitionLevels);

    protected sealed override void WriteCore(ColumnWriter writer, ReadOnlySpan<TValue> values, ReadOnlySpan<int> definitionLevels, ReadOnlySpan<int> repetitionLevels) =>
        PhysicalConversion.Write<TPhysical, TValue, TConverter>(writer, TConverter.Create(writer.Column), values, definitionLevels, repetitionLevels);
}
