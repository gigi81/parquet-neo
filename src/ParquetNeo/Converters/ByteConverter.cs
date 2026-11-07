using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Converters;

/// <summary>Reads and writes an unsigned 8-bit INTEGER column (UINT_8) as byte.</summary>
internal readonly struct ByteConverter : IValueConverter<int, byte, ByteConverter>
{
    public static ByteConverter Create(PrimitiveNode column)
    {
        ConversionRules.RequireInteger(column, PhysicalType.Int32, 8, isSigned: false, "byte");
        return default;
    }

    public byte ToValue(int physical) => physical is >= 0 and <= byte.MaxValue ? (byte)physical : throw OutOfRange(physical);

    public int ToPhysical(byte value) => value;

    private static ParquetFormatException OutOfRange(int physical) =>
        new($"The value {physical} is outside the range of the {typeof(byte).Name} its column is stored as.");
}
