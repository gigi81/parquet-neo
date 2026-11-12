using ParquetNeo.Schema;

namespace ParquetNeo.Nested;

/// <summary>Where the parts of a list are in the schema; see <see cref="NestedSchema.GetListLayout"/>.</summary>
/// <param name="Container">The LIST group, or the repeated field itself for an implicit list.</param>
/// <param name="Repeated">The repeated node: its elements are the elements of the list.</param>
/// <param name="Element">The node that describes an element: below <paramref name="Repeated"/> in a standard list, otherwise the repeated node itself.</param>
/// <param name="Style">How the list is laid out.</param>
public sealed record ListLayout(SchemaNode Container, SchemaNode Repeated, SchemaNode Element, ListStyle Style)
{
    /// <summary>Whether an element can be null: only the elements of a standard list can.</summary>
    public bool ElementIsNullable => Style == ListStyle.ThreeLevel && Element.Repetition == FieldRepetitionType.Optional;

    /// <summary>Whether the list itself can be null (the container is optional).</summary>
    public bool ListIsNullable => Container.Repetition == FieldRepetitionType.Optional;
}
