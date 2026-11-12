using ParquetNeo.Core;
using ParquetNeo.Schema;

namespace ParquetNeo.ColumnTypes;

/// <summary>A FLOAT column as <see cref="float"/>.</summary>
internal sealed class FloatColumnType : ColumnType<float>
{
    public override string Name => "float";

    public override PrimitiveNode CreateNode(string name, FieldRepetitionType repetition) => new(name, PhysicalType.Float, repetition);

    public override string? GetProblem(PrimitiveNode column) => column.Type == PhysicalType.Float ? null : ColumnProblems.Mismatch(column, Name);

    protected override BatchRead ReadCore(ColumnReader reader, Span<float> values, Span<int> definitionLevels, Span<int> repetitionLevels) =>
        reader.ReadBatch(values, definitionLevels, repetitionLevels);

    protected override void WriteCore(ColumnWriter writer, ReadOnlySpan<float> values, ReadOnlySpan<int> definitionLevels, ReadOnlySpan<int> repetitionLevels) =>
        writer.WriteBatch(values, definitionLevels, repetitionLevels);
}
