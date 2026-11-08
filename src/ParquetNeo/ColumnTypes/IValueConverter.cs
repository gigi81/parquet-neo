namespace ParquetNeo.ColumnTypes;

/// <summary>
/// Converts one value at a time between the <see cref="int"/> (INT32) or <see cref="long"/> (INT64) that a column's physical type
/// reads and writes as, <typeparamref name="TPhysical"/>, and the .NET type <typeparamref name="TValue"/>. Implemented by a column
/// type whose conversion does not depend on the column, or by a small struct made for a column that has been checked and holds
/// what the column says (a scale, a unit). It is a type argument of the batch loops, so the JIT specializes them for it.
/// </summary>
internal interface IValueConverter<TPhysical, TValue>
    where TPhysical : unmanaged
{
    /// <exception cref="Exceptions.ValueConversionException">The stored value cannot be represented.</exception>
    TValue ToValue(TPhysical physical);

    /// <exception cref="ArgumentOutOfRangeException">The value cannot be stored in the column.</exception>
    TPhysical ToPhysical(TValue value);
}
