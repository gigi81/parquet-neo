using System.Buffers.Binary;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Logical;

/// <summary>Reads and writes a FLOAT16 column (FIXED_LEN_BYTE_ARRAY of 2 bytes, little endian IEEE 754 half precision) as <see cref="Half"/>.</summary>
internal readonly struct HalfConverter : IFixedLengthConverter<Half, HalfConverter>
{
    public static HalfConverter Create(PrimitiveNode column)
    {
        ConversionRules.Require(column, PhysicalType.FixedLenByteArray, LogicalTypeKind.Float16, nameof(Half), "FLOAT16 column stored as FIXED_LEN_BYTE_ARRAY");
        return default;
    }

    public int Width => 2;

    public Half ToValue(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadHalfLittleEndian(bytes);

    public void ToBytes(Half value, Span<byte> bytes) => BinaryPrimitives.WriteHalfLittleEndian(bytes, value);
}
