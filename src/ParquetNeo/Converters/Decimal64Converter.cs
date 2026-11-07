using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Converters;

/// <summary>Reads and writes a DECIMAL column stored as INT64 (precision up to 18) as <see cref="decimal"/>.</summary>
internal readonly struct Decimal64Converter : IValueConverter<long, decimal, Decimal64Converter>
{
    private readonly int _scale;
    private readonly int _precision;

    private Decimal64Converter(int scale, int precision)
    {
        _scale = scale;
        _precision = precision;
    }

    public static Decimal64Converter Create(PrimitiveNode column)
    {
        ConversionRules.RequireDecimal(column, PhysicalType.Int64);
        return new(column.LogicalType!.Scale, column.LogicalType.Precision);
    }

    public decimal ToValue(long physical) => DecimalConversion.FromInt64(physical, _scale);

    public long ToPhysical(decimal value) => (long)DecimalConversion.ToUnscaled(value, _scale, _precision);
}
