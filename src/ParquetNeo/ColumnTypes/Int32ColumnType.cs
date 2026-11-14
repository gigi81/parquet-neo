using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.ColumnTypes;

/// <summary>An INT32 column, plain or annotated as a signed integer of up to 32 bits, as <see cref="int"/>.</summary>
internal sealed class Int32ColumnType : ColumnType<int>
{
    public override string Name => "int";

    public override PrimitiveNode CreateNode(string name, FieldRepetitionType repetition) => new(name, PhysicalType.Int32, repetition);

    public override string? GetProblem(PrimitiveNode column) =>
        column.Type == PhysicalType.Int32 && column.LogicalType is null or { Kind: LogicalTypeKind.Integer, IsSigned: true } ? null : ColumnProblems.Mismatch(column, Name);

    protected override BatchRead ReadCore(ColumnReader reader, Span<int> values, Span<int> definitionLevels, Span<int> repetitionLevels) =>
        reader.ReadBatch(values, definitionLevels, repetitionLevels);

    protected override void WriteCore(ColumnWriter writer, ReadOnlySpan<int> values, ReadOnlySpan<int> definitionLevels, ReadOnlySpan<int> repetitionLevels) =>
        writer.WriteBatch(values, definitionLevels, repetitionLevels);
}
