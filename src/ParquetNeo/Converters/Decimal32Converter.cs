using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Converters;

/// <summary>Reads and writes a DECIMAL column stored as INT32 (precision up to 9) as <see cref="decimal"/>.</summary>
internal readonly struct Decimal32Converter : IValueConverter<int, decimal, Decimal32Converter>
{
    private readonly int _scale;
    private readonly int _precision;

    private Decimal32Converter(int scale, int precision)
    {
        _scale = scale;
        _precision = precision;
    }

    public static Decimal32Converter Create(PrimitiveNode column)
    {
        ConversionRules.RequireDecimal(column, PhysicalType.Int32);
        return new(column.LogicalType!.Scale, column.LogicalType.Precision);
    }

    public decimal ToValue(int physical) => DecimalConversion.FromInt64(physical, _scale);

    public int ToPhysical(decimal value) => (int)DecimalConversion.ToUnscaled(value, _scale, _precision);
}
