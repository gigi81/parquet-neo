using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.ColumnTypes;

/// <summary>An INT64 column, plain or annotated as a signed 64-bit integer, as <see cref="long"/>.</summary>
internal sealed class Int64ColumnType : ColumnType<long>
{
    public override string Name => "long";

    public override PrimitiveNode CreateNode(string name, FieldRepetitionType repetition) => new(name, PhysicalType.Int64, repetition);

    public override string? GetProblem(PrimitiveNode column) =>
        column.Type == PhysicalType.Int64 && column.LogicalType is null or { Kind: LogicalTypeKind.Integer, IsSigned: true } ? null : ColumnProblems.Mismatch(column, Name);

    protected override BatchRead ReadCore(ColumnReader reader, Span<long> values, Span<int> definitionLevels, Span<int> repetitionLevels) =>
        reader.ReadBatch(values, definitionLevels, repetitionLevels);

    protected override void WriteCore(ColumnWriter writer, ReadOnlySpan<long> values, ReadOnlySpan<int> definitionLevels, ReadOnlySpan<int> repetitionLevels) =>
        writer.WriteBatch(values, definitionLevels, repetitionLevels);
}
