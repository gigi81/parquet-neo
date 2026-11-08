using ParquetNeo.Schema;

namespace ParquetNeo.ColumnTypes;

/// <summary>Converts between the FIXED_LEN_BYTE_ARRAY values of a column and the .NET type <typeparamref name="TValue"/>; see <see cref="IValueConverter{TPhysical, TValue, TSelf}"/>.</summary>
internal interface IFixedLengthConverter<TValue, TSelf>
    where TSelf : IFixedLengthConverter<TValue, TSelf>
{
    static abstract TSelf Create(PrimitiveNode column);

    /// <summary>The number of bytes of each value.</summary>
    int Width { get; }

    TValue ToValue(ReadOnlySpan<byte> bytes);

    void ToBytes(TValue value, Span<byte> bytes);
}
