using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Converters;

/// <summary>Reads and writes a DECIMAL column stored as FIXED_LEN_BYTE_ARRAY (big-endian two's complement) as <see cref="decimal"/>.</summary>
internal readonly struct DecimalFixedConverter : IFixedLengthConverter<decimal, DecimalFixedConverter>
{
    private readonly int _scale;
    private readonly int _precision;

    private DecimalFixedConverter(int width, int scale, int precision)
    {
        Width = width;
        _scale = scale;
        _precision = precision;
    }

    public static DecimalFixedConverter Create(PrimitiveNode column)
    {
        ConversionRules.RequireDecimal(column, PhysicalType.FixedLenByteArray);
        return new(column.TypeLength!.Value, column.LogicalType!.Scale, column.LogicalType.Precision);
    }

    public int Width { get; }

    public decimal ToValue(ReadOnlySpan<byte> bytes) => DecimalConversion.FromBigEndian(bytes, _scale);

    public void ToBytes(decimal value, Span<byte> bytes) =>
        DecimalConversion.WriteBigEndian(DecimalConversion.ToUnscaled(value, _scale, _precision), bytes);
}
