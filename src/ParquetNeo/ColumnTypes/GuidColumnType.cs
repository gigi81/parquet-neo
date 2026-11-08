using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.ColumnTypes;

/// <summary>A UUID column (FIXED_LEN_BYTE_ARRAY of 16 bytes in RFC 4122 order) as <see cref="Guid"/>.</summary>
internal sealed class GuidColumnType : FixedConvertedColumnType<Guid, GuidColumnType.Converter>
{
    public override PrimitiveNode CreateNode(string name, FieldRepetitionType repetition) => new(name, PhysicalType.FixedLenByteArray, repetition, LogicalType.Uuid, typeLength: 16);

    public override string? GetProblem(PrimitiveNode column) =>
        column.Type == PhysicalType.FixedLenByteArray && column.LogicalType is { Kind: LogicalTypeKind.Uuid } ? null : ColumnProblems.Mismatch(column, Name);

    internal readonly struct Converter : IFixedLengthConverter<Guid, Converter>
    {
        public static Converter Create(PrimitiveNode column) => default;

        public int Width => 16;

        public Guid ToValue(ReadOnlySpan<byte> bytes) => new(bytes, bigEndian: true);

        public void ToBytes(Guid value, Span<byte> bytes) => value.TryWriteBytes(bytes, bigEndian: true, out _);
    }
}
