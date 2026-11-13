namespace ParquetNeo.Serialization.Generated;

/// <summary>How rows of a type are read, made by a <see cref="ReadMappingBuilder{TRow}"/>.</summary>
public sealed class ReadMapping<TRow>
{
    internal ReadMapping(ReadModel<TRow> model) => Model = model;

    internal ReadModel<TRow> Model { get; }
}
