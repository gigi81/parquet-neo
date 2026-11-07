using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Converters;

/// <summary>Reads and writes an unsigned 32-bit INTEGER column (UINT_32), stored as the signed bits of an INT32, as uint.</summary>
internal readonly struct UInt32Converter : IValueConverter<int, uint, UInt32Converter>
{
    public static UInt32Converter Create(PrimitiveNode column)
    {
        ConversionRules.RequireInteger(column, PhysicalType.Int32, 32, isSigned: false, "uint");
        return default;
    }

    public uint ToValue(int physical) => unchecked((uint)physical);

    public int ToPhysical(uint value) => unchecked((int)value);
}
