
namespace ParquetNeo.ColumnTypes;

/// <summary>
/// A column type whose values are converted from and to the INT32 or INT64 of the column one by one, and that does the conversion
/// itself: it is its own <see cref="IValueConverter{TPhysical, TValue}"/>. <typeparamref name="TSelf"/> is the derived type, which
/// is sealed, so that the batch loops are specialized for it and the per-value calls are not virtual.
/// </summary>
internal abstract class ConvertedColumnType<TValue, TPhysical, TSelf> : ColumnType<TValue>, IValueConverter<TPhysical, TValue>
    where TPhysical : unmanaged
    where TSelf : ConvertedColumnType<TValue, TPhysical, TSelf>
{
    public abstract TValue ToValue(TPhysical physical);

    public abstract TPhysical ToPhysical(TValue value);

    protected sealed override BatchRead ReadCore(ColumnReader reader, Span<TValue> values, Span<int> definitionLevels, Span<int> repetitionLevels) =>
        PhysicalConversion.Read<TPhysical, TValue, TSelf>(reader, (TSelf)this, values, definitionLevels, repetitionLevels);

    protected sealed override void WriteCore(ColumnWriter writer, ReadOnlySpan<TValue> values, ReadOnlySpan<int> definitionLevels, ReadOnlySpan<int> repetitionLevels) =>
        PhysicalConversion.Write<TPhysical, TValue, TSelf>(writer, (TSelf)this, values, definitionLevels, repetitionLevels);
}
