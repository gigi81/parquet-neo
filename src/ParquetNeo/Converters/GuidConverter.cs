using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Converters;

/// <summary>Reads and writes a UUID column (FIXED_LEN_BYTE_ARRAY of 16 bytes in RFC 4122 order) as <see cref="Guid"/>.</summary>
internal readonly struct GuidConverter : IFixedLengthConverter<Guid, GuidConverter>
{
    public static GuidConverter Create(PrimitiveNode column)
    {
        ConversionRules.Require(column, PhysicalType.FixedLenByteArray, LogicalTypeKind.Uuid, nameof(Guid), "UUID column stored as FIXED_LEN_BYTE_ARRAY");
        return default;
    }

    public int Width => 16;

    public Guid ToValue(ReadOnlySpan<byte> bytes) => new(bytes, bigEndian: true);

    public void ToBytes(Guid value, Span<byte> bytes) => value.TryWriteBytes(bytes, bigEndian: true, out _);
}
