
namespace ParquetNeo.ColumnTypes;

/// <summary>
/// A column type whose values are converted from and to the FIXED_LEN_BYTE_ARRAY of the column one by one, and that does the
/// conversion itself: it is its own <see cref="IFixedLengthConverter{TValue}"/>. <typeparamref name="TSelf"/> is the derived type,
/// which is sealed, so that the batch loops are specialized for it.
/// </summary>
internal abstract class FixedConvertedColumnType<TValue, TSelf> : ColumnType<TValue>, IFixedLengthConverter<TValue>
    where TSelf : FixedConvertedColumnType<TValue, TSelf>
{
    public abstract int Width { get; }

    public abstract TValue ToValue(ReadOnlySpan<byte> bytes);

    public abstract void ToBytes(TValue value, Span<byte> bytes);

    protected sealed override BatchRead ReadCore(ColumnReader reader, Span<TValue> values, Span<int> definitionLevels, Span<int> repetitionLevels) =>
        PhysicalConversion.ReadFixed<TValue, TSelf>(reader, (TSelf)this, values, definitionLevels, repetitionLevels);

    protected sealed override void WriteCore(ColumnWriter writer, ReadOnlySpan<TValue> values, ReadOnlySpan<int> definitionLevels, ReadOnlySpan<int> repetitionLevels) =>
        PhysicalConversion.WriteFixed<TValue, TSelf>(writer, (TSelf)this, values, definitionLevels, repetitionLevels);
}
