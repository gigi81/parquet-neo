using ParquetNeo.Core;
using ParquetNeo.Schema;

namespace ParquetNeo.ColumnTypes;

/// <summary>A BOOLEAN column as <see cref="bool"/>.</summary>
internal sealed class BooleanColumnType : ColumnType<bool>
{
    public override string Name => "bool";

    public override PrimitiveNode CreateNode(string name, FieldRepetitionType repetition) => new(name, PhysicalType.Boolean, repetition);

    public override string? GetProblem(PrimitiveNode column) => column.Type == PhysicalType.Boolean ? null : ColumnProblems.Mismatch(column, Name);

    protected override BatchRead ReadCore(ColumnReader reader, Span<bool> values, Span<int> definitionLevels, Span<int> repetitionLevels) =>
        reader.ReadBatch(values, definitionLevels, repetitionLevels);

    protected override void WriteCore(ColumnWriter writer, ReadOnlySpan<bool> values, ReadOnlySpan<int> definitionLevels, ReadOnlySpan<int> repetitionLevels) =>
        writer.WriteBatch(values, definitionLevels, repetitionLevels);
}
