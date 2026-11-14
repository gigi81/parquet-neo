using ParquetNeo.Schema;

namespace ParquetNeo.ColumnTypes;

/// <summary>A DOUBLE column as <see cref="double"/>.</summary>
internal sealed class DoubleColumnType : ColumnType<double>
{
    public override string Name => "double";

    public override PrimitiveNode CreateNode(string name, FieldRepetitionType repetition) => new(name, PhysicalType.Double, repetition);

    public override string? GetProblem(PrimitiveNode column) => column.Type == PhysicalType.Double ? null : ColumnProblems.Mismatch(column, Name);

    protected override BatchRead ReadCore(ColumnReader reader, Span<double> values, Span<int> definitionLevels, Span<int> repetitionLevels) =>
        reader.ReadBatch(values, definitionLevels, repetitionLevels);

    protected override void WriteCore(ColumnWriter writer, ReadOnlySpan<double> values, ReadOnlySpan<int> definitionLevels, ReadOnlySpan<int> repetitionLevels) =>
        writer.WriteBatch(values, definitionLevels, repetitionLevels);
}
