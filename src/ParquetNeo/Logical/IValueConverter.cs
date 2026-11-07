using ParquetNeo.Schema;

namespace ParquetNeo.Logical;

/// <summary>
/// Converts one value at a time between the <see cref="int"/> (INT32) or <see cref="long"/> (INT64) that the column's
/// physical type reads and writes as, <typeparamref name="TPhysical"/>, and the .NET type of its logical type annotation, <typeparamref name="TValue"/>.
/// </summary>
internal interface IValueConverter<TPhysical, TValue, TSelf>
    where TPhysical : unmanaged
    where TSelf : IValueConverter<TPhysical, TValue, TSelf>
{
    /// <summary>Checks that the column can be read or written as <typeparamref name="TValue"/> and returns the converter for it.</summary>
    /// <exception cref="ArgumentException">The column is not of the logical and physical type this conversion needs.</exception>
    static abstract TSelf Create(PrimitiveNode column);

    /// <exception cref="ParquetFormatException">The stored value cannot be represented.</exception>
    TValue ToValue(TPhysical physical);

    /// <exception cref="ArgumentOutOfRangeException">The value cannot be stored in the column.</exception>
    TPhysical ToPhysical(TValue value);
}
