using ParquetNeo.Exceptions;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.ColumnTypes;

/// <summary>An unsigned 8-bit INTEGER column (UINT_8) as <see cref="byte"/>.</summary>
internal sealed class ByteColumnType : ConvertedColumnType<byte, int, ByteColumnType>
{
    public override string Name => "byte";

    public override PrimitiveNode CreateNode(string name, FieldRepetitionType repetition) => new(name, PhysicalType.Int32, repetition, LogicalType.Integer(8, false));

    public override string? GetProblem(PrimitiveNode column) =>
        column.Type == PhysicalType.Int32 && column.LogicalType is { Kind: LogicalTypeKind.Integer, BitWidth: 8, IsSigned: false }
            ? null
            : ColumnProblems.Mismatch(column, Name);

    public override byte ToValue(int physical) => physical is >= 0 and <= byte.MaxValue ? (byte)physical : throw OutOfRange(physical);

    public override int ToPhysical(byte value) => value;

    private static ValueConversionException OutOfRange(int physical) =>
        new($"The value {physical} is outside the range of the {typeof(byte).Name} its column is stored as.");
}
