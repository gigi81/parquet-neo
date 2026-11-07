using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Converters;

/// <summary>
/// Reads and writes a DECIMAL column stored as BYTE_ARRAY as <see cref="decimal"/>. The values have no fixed width, so this does
/// not implement <see cref="IFixedLengthConverter{TValue, TSelf}"/>: the column reader and writer handle the arrays themselves.
/// </summary>
internal readonly struct DecimalByteArrayConverter
{
    /// <summary>The most bytes the shortest form of a value takes.</summary>
    public const int MaxLength = 16;

    private readonly int _scale;
    private readonly int _precision;

    private DecimalByteArrayConverter(int scale, int precision)
    {
        _scale = scale;
        _precision = precision;
    }

    public static DecimalByteArrayConverter Create(PrimitiveNode column)
    {
        ConversionRules.RequireDecimal(column, PhysicalType.ByteArray);
        return new(column.LogicalType!.Scale, column.LogicalType.Precision);
    }

    public decimal ToValue(ReadOnlySpan<byte> bytes) => DecimalConversion.FromBigEndian(bytes, _scale);

    /// <summary>Writes the value in its shortest form to the start of <paramref name="bytes"/> (at least <see cref="MaxLength"/> long) and returns the length used.</summary>
    public int ToBytes(decimal value, Span<byte> bytes)
    {
        var unscaled = DecimalConversion.ToUnscaled(value, _scale, _precision);
        int length = DecimalConversion.MinimalLength(unscaled);
        DecimalConversion.WriteBigEndian(unscaled, bytes[..length]);
        return length;
    }
}
