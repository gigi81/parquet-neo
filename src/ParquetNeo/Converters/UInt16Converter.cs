using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Converters;

/// <summary>Reads and writes an unsigned 16-bit INTEGER column (UINT_16) as ushort.</summary>
internal readonly struct UInt16Converter : IValueConverter<int, ushort, UInt16Converter>
{
    public static UInt16Converter Create(PrimitiveNode column)
    {
        ConversionRules.RequireInteger(column, PhysicalType.Int32, 16, isSigned: false, "ushort");
        return default;
    }

    public ushort ToValue(int physical) => physical is >= 0 and <= ushort.MaxValue ? (ushort)physical : throw OutOfRange(physical);

    public int ToPhysical(ushort value) => value;

    private static ParquetFormatException OutOfRange(int physical) =>
        new($"The value {physical} is outside the range of the {typeof(ushort).Name} its column is stored as.");
}
