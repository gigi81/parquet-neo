using ParquetNeo.Exceptions;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.ColumnTypes;

/// <summary>A signed 16-bit INTEGER column (INT_16) as <see cref="short"/>.</summary>
internal sealed class Int16ColumnType : ConvertedColumnType<short, int, Int16ColumnType>
{
    public override string Name => "short";

    public override PrimitiveNode CreateNode(string name, FieldRepetitionType repetition) => new(name, PhysicalType.Int32, repetition, LogicalType.Integer(16, true));

    public override string? GetProblem(PrimitiveNode column) =>
        column.Type == PhysicalType.Int32 && column.LogicalType is { Kind: LogicalTypeKind.Integer, BitWidth: 16, IsSigned: true }
            ? null
            : ColumnProblems.Mismatch(column, Name);

    public override short ToValue(int physical) => physical is >= short.MinValue and <= short.MaxValue ? (short)physical : throw OutOfRange(physical);

    public override int ToPhysical(short value) => value;

    private static ValueConversionException OutOfRange(int physical) =>
        new($"The value {physical} is outside the range of the {typeof(short).Name} its column is stored as.");
}
