using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Converters;

/// <summary>Reads and writes a signed 8-bit INTEGER column (INT_8) as sbyte.</summary>
internal readonly struct SByteConverter : IValueConverter<int, sbyte, SByteConverter>
{
    public static SByteConverter Create(PrimitiveNode column)
    {
        ConversionRules.RequireInteger(column, PhysicalType.Int32, 8, isSigned: true, "sbyte");
        return default;
    }

    public sbyte ToValue(int physical) => physical is >= sbyte.MinValue && physical <= sbyte.MaxValue ? (sbyte)physical : throw OutOfRange(physical);

    public int ToPhysical(sbyte value) => value;

    private static ParquetFormatException OutOfRange(int physical) =>
        new($"The value {physical} is outside the range of the {typeof(sbyte).Name} its column is stored as.");
}
