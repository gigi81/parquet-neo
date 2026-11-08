using ParquetNeo.Schema;

namespace ParquetNeo.ColumnTypes;

/// <summary>
/// Converts one value at a time between the <see cref="int"/> (INT32) or <see cref="long"/> (INT64) that a column's physical type
/// reads and writes as, <typeparamref name="TPhysical"/>, and the .NET type <typeparamref name="TValue"/>. It is created for a
/// column that has been checked and holds what the column says (a scale, a unit), so it is a struct that the JIT inlines.
/// </summary>
internal interface IValueConverter<TPhysical, TValue, TSelf>
    where TPhysical : unmanaged
    where TSelf : IValueConverter<TPhysical, TValue, TSelf>
{
    static abstract TSelf Create(PrimitiveNode column);

    /// <exception cref="Exceptions.ValueConversionException">The stored value cannot be represented.</exception>
    TValue ToValue(TPhysical physical);

    /// <exception cref="ArgumentOutOfRangeException">The value cannot be stored in the column.</exception>
    TPhysical ToPhysical(TValue value);
}
