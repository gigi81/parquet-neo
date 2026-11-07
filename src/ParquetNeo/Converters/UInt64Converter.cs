using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Converters;

/// <summary>Reads and writes an unsigned 64-bit INTEGER column (UINT_64), stored as the signed bits of an INT64, as ulong.</summary>
internal readonly struct UInt64Converter : IValueConverter<long, ulong, UInt64Converter>
{
    public static UInt64Converter Create(PrimitiveNode column)
    {
        ConversionRules.RequireInteger(column, PhysicalType.Int64, 64, isSigned: false, "ulong");
        return default;
    }

    public ulong ToValue(long physical) => unchecked((ulong)physical);

    public long ToPhysical(ulong value) => unchecked((long)value);
}
