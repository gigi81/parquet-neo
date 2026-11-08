namespace ParquetNeo.ColumnTypes;

/// <summary>Converts between the FIXED_LEN_BYTE_ARRAY values of a column and the .NET type <typeparamref name="TValue"/>; see <see cref="IValueConverter{TPhysical, TValue}"/>.</summary>
internal interface IFixedLengthConverter<TValue>
{
    /// <summary>The number of bytes of each value.</summary>
    int Width { get; }

    TValue ToValue(ReadOnlySpan<byte> bytes);

    void ToBytes(TValue value, Span<byte> bytes);
}
