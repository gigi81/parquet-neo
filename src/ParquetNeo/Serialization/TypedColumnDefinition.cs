using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Serialization;

/// <summary>A column definition whose values have the .NET type <typeparamref name="TValue"/>.</summary>
internal sealed class TypedColumnDefinition<TRow, TValue>(
    string name,
    string property,
    bool optional,
    ColumnShape shape,
    TryGetter<TRow, TValue> getter,
    BatchWriter<TValue> writer,
    ParquetEncoding? encoding) : ColumnDefinition<TRow>(name, encoding)
{
    public string Property { get; } = property;

    public bool Optional { get; } = optional;

    public TryGetter<TRow, TValue> Getter { get; } = getter;

    public BatchWriter<TValue> Writer { get; } = writer;

    public override PrimitiveNode CreateNode() =>
        new(Name, shape.Type, Optional ? FieldRepetitionType.Optional : FieldRepetitionType.Required, shape.LogicalType, shape.TypeLength);

    public override IColumnBuffer<TRow> CreateBuffer(int rowGroupSize) => new ColumnBuffer<TRow, TValue>(this, rowGroupSize);
}
