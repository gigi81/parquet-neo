namespace ParquetNeo.Serialization.Generated;

/// <summary>How rows of a type are written, made by a <see cref="WriteMappingBuilder{TRow}"/>.</summary>
public sealed class WriteMapping<TRow>
{
    internal WriteMapping(RowModel<TRow> model) => Model = model;

    internal RowModel<TRow> Model { get; }
}
