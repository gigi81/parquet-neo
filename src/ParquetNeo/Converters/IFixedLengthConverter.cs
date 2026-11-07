using ParquetNeo.Schema;

namespace ParquetNeo.Converters;

/// <summary>Converts between the FIXED_LEN_BYTE_ARRAY values of a column and the .NET type its logical type stands for.</summary>
internal interface IFixedLengthConverter<TValue, TSelf>
    where TSelf : IFixedLengthConverter<TValue, TSelf>
{
    /// <summary>Checks that the column can be read or written as <typeparamref name="TValue"/> and returns the converter for it.</summary>
    /// <exception cref="ArgumentException">The column is not of the logical and physical type this conversion needs.</exception>
    static abstract TSelf Create(PrimitiveNode column);

    /// <summary>The number of bytes of each value.</summary>
    int Width { get; }

    TValue ToValue(ReadOnlySpan<byte> bytes);

    void ToBytes(TValue value, Span<byte> bytes);
}
