using System.Buffers.Binary;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.ColumnTypes;

/// <summary>A FLOAT16 column (FIXED_LEN_BYTE_ARRAY of 2 bytes, little endian IEEE 754 half precision) as <see cref="Half"/>.</summary>
internal sealed class HalfColumnType : FixedConvertedColumnType<Half, HalfColumnType>
{
    public override PrimitiveNode CreateNode(string name, FieldRepetitionType repetition) => new(name, PhysicalType.FixedLenByteArray, repetition, LogicalType.Float16, typeLength: 2);

    public override string? GetProblem(PrimitiveNode column) =>
        column.Type == PhysicalType.FixedLenByteArray && column.LogicalType is { Kind: LogicalTypeKind.Float16 } ? null : ColumnProblems.Mismatch(column, Name);

    public override int Width => 2;

    public override Half ToValue(ReadOnlySpan<byte> bytes) => BinaryPrimitives.ReadHalfLittleEndian(bytes);

    public override void ToBytes(Half value, Span<byte> bytes) => BinaryPrimitives.WriteHalfLittleEndian(bytes, value);
}
