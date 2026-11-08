using ParquetNeo.Exceptions;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.ColumnTypes;

/// <summary>A signed 8-bit INTEGER column (INT_8) as <see cref="sbyte"/>.</summary>
internal sealed class SByteColumnType : ConvertedColumnType<sbyte, int, SByteColumnType>
{
    public override string Name => "sbyte";

    public override PrimitiveNode CreateNode(string name, FieldRepetitionType repetition) => new(name, PhysicalType.Int32, repetition, LogicalType.Integer(8, true));

    public override string? GetProblem(PrimitiveNode column) =>
        column.Type == PhysicalType.Int32 && column.LogicalType is { Kind: LogicalTypeKind.Integer, BitWidth: 8, IsSigned: true }
            ? null
            : ColumnProblems.Mismatch(column, Name);

    public override sbyte ToValue(int physical) => physical is >= sbyte.MinValue and <= sbyte.MaxValue ? (sbyte)physical : throw OutOfRange(physical);

    public override int ToPhysical(sbyte value) => value;

    private static ValueConversionException OutOfRange(int physical) =>
        new($"The value {physical} is outside the range of the {typeof(sbyte).Name} its column is stored as.");
}
