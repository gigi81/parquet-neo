using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Converters;

/// <summary>Reads and writes a signed 16-bit INTEGER column (INT_16) as short.</summary>
internal readonly struct Int16Converter : IValueConverter<int, short, Int16Converter>
{
    public static Int16Converter Create(PrimitiveNode column)
    {
        ConversionRules.RequireInteger(column, PhysicalType.Int32, 16, isSigned: true, "short");
        return default;
    }

    public short ToValue(int physical) => physical is >= short.MinValue and <= short.MaxValue ? (short)physical : throw OutOfRange(physical);

    public int ToPhysical(short value) => value;

    private static ParquetFormatException OutOfRange(int physical) =>
        new($"The value {physical} is outside the range of the {typeof(short).Name} its column is stored as.");
}
