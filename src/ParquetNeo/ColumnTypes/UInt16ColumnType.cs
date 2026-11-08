using ParquetNeo.Exceptions;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.ColumnTypes;

/// <summary>An unsigned 16-bit INTEGER column (UINT_16) as <see cref="ushort"/>.</summary>
internal sealed class UInt16ColumnType : ConvertedColumnType<ushort, int, UInt16ColumnType>
{
    public override string Name => "ushort";

    public override PrimitiveNode CreateNode(string name, FieldRepetitionType repetition) => new(name, PhysicalType.Int32, repetition, LogicalType.Integer(16, false));

    public override string? GetProblem(PrimitiveNode column) =>
        column.Type == PhysicalType.Int32 && column.LogicalType is { Kind: LogicalTypeKind.Integer, BitWidth: 16, IsSigned: false }
            ? null
            : ColumnProblems.Mismatch(column, Name);

    public override ushort ToValue(int physical) => physical is >= 0 and <= ushort.MaxValue ? (ushort)physical : throw OutOfRange(physical);

    public override int ToPhysical(ushort value) => value;

    private static ValueConversionException OutOfRange(int physical) =>
        new($"The value {physical} is outside the range of the {typeof(ushort).Name} its column is stored as.");
}
