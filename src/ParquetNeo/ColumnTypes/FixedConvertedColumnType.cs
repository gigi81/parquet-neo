using ParquetNeo.Core;

namespace ParquetNeo.ColumnTypes;

/// <summary>A column type whose values are converted from and to the FIXED_LEN_BYTE_ARRAY of the column one by one by a struct converter.</summary>
internal abstract class FixedConvertedColumnType<TValue, TConverter> : ColumnType<TValue>
    where TConverter : struct, IFixedLengthConverter<TValue, TConverter>
{
    protected sealed override BatchRead ReadCore(ColumnReader reader, Span<TValue> values, Span<int> definitionLevels, Span<int> repetitionLevels) =>
        PhysicalConversion.ReadFixed<TValue, TConverter>(reader, TConverter.Create(reader.Column), values, definitionLevels, repetitionLevels);

    protected sealed override void WriteCore(ColumnWriter writer, ReadOnlySpan<TValue> values, ReadOnlySpan<int> definitionLevels, ReadOnlySpan<int> repetitionLevels) =>
        PhysicalConversion.WriteFixed<TValue, TConverter>(writer, TConverter.Create(writer.Column), values, definitionLevels, repetitionLevels);
}
