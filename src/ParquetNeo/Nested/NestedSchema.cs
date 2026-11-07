using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Nested;

/// <summary>
/// Builds the standard LIST and MAP layouts and recognizes the layouts found in files, including the legacy ones.
/// </summary>
public static class NestedSchema
{
    /// <summary>
    /// A list in the standard layout: a LIST group <paramref name="name"/> holding a repeated group <paramref name="repeatedName"/>
    /// that holds <paramref name="element"/>. Make the list nullable with an optional <paramref name="repetition"/> and the
    /// elements nullable with an optional <paramref name="element"/>.
    /// </summary>
    /// <param name="element">By convention named <c>element</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="repetition"/> is repeated, or <paramref name="element"/> is repeated (use a list of lists).</exception>
    public static GroupNode List(string name, FieldRepetitionType repetition, SchemaNode element, string repeatedName = "list", int? fieldId = null)
    {
        ArgumentNullException.ThrowIfNull(element);
        RequireNotRepeated(repetition, nameof(repetition));
        RequireNotRepeated(element.Repetition, nameof(element));
        return new GroupNode(name, repetition, [new GroupNode(repeatedName, FieldRepetitionType.Repeated, [element])], LogicalType.List, fieldId);
    }

    /// <summary>
    /// A map in the standard layout: a MAP group <paramref name="name"/> holding a repeated group <paramref name="repeatedName"/>
    /// that holds <paramref name="key"/> and <paramref name="value"/>.
    /// </summary>
    /// <param name="key">Required; by convention named <c>key</c>.</param>
    /// <param name="value">Required or optional; by convention named <c>value</c>.</param>
    /// <exception cref="ArgumentException">The key is not required, or the map or the value is repeated.</exception>
    public static GroupNode Map(string name, FieldRepetitionType repetition, SchemaNode key, SchemaNode value, string repeatedName = "key_value", int? fieldId = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        RequireNotRepeated(repetition, nameof(repetition));
        RequireNotRepeated(value.Repetition, nameof(value));
        if (key.Repetition != FieldRepetitionType.Required)
        {
            throw new ArgumentException("The key of a map must be required.", nameof(key));
        }

        return new GroupNode(name, repetition, [new GroupNode(repeatedName, FieldRepetitionType.Repeated, [key, value])], LogicalType.Map, fieldId);
    }

    /// <summary>
    /// Finds the parts of the list that <paramref name="node"/> is, or returns null when it is not a list. A node is a list
    /// when it is a group annotated LIST with the shape of one of the <see cref="ListStyle"/> layouts, or a repeated field
    /// (which is a list of required elements; pass the LIST group rather than the repeated node inside it).
    /// </summary>
    public static ListLayout? GetListLayout(SchemaNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node is GroupNode { LogicalType.Kind: LogicalTypeKind.List } group)
        {
            return GetListLayout(group);
        }

        return node.Repetition == FieldRepetitionType.Repeated ? new ListLayout(node, node, node, ListStyle.Implicit) : null;
    }

    private static ListLayout? GetListLayout(GroupNode group)
    {
        // A list group holds exactly one field, the repeated one.
        if (group.Children is not [{ Repetition: FieldRepetitionType.Repeated } repeated])
        {
            return null;
        }

        if (repeated is not GroupNode repeatedGroup)
        {
            return new ListLayout(group, repeated, repeated, ListStyle.RepeatedPrimitive);
        }

        if (repeatedGroup.Children.Count != 1)
        {
            return new ListLayout(group, repeated, repeated, ListStyle.RepeatedStruct);
        }

        if (repeatedGroup.Name == "array" || repeatedGroup.Name == group.Name + "_tuple")
        {
            return new ListLayout(group, repeated, repeated, ListStyle.RepeatedTuple);
        }

        return new ListLayout(group, repeated, repeatedGroup.Children[0], ListStyle.ThreeLevel);
    }

    /// <summary>
    /// Finds the parts of the map that <paramref name="node"/> is, or returns null when it is not a map: a group annotated
    /// MAP (or the legacy MAP_KEY_VALUE) that holds one repeated group of a required key and, normally, a value.
    /// </summary>
    public static MapLayout? GetMapLayout(SchemaNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node is not GroupNode group
            || (group.LogicalType?.Kind != LogicalTypeKind.Map && group.ConvertedType != ConvertedType.MapKeyValue))
        {
            return null;
        }

        if (group.Children is not [GroupNode { Repetition: FieldRepetitionType.Repeated } keyValue])
        {
            return null;
        }

        if (keyValue.Children.Count is not (1 or 2) || keyValue.Children[0].Repetition != FieldRepetitionType.Required)
        {
            return null;
        }

        return new MapLayout(group, keyValue, keyValue.Children[0], keyValue.Children.Count == 2 ? keyValue.Children[1] : null);
    }

    private static void RequireNotRepeated(FieldRepetitionType repetition, string parameter)
    {
        if (repetition == FieldRepetitionType.Repeated)
        {
            throw new ArgumentException("A list, map, element or value cannot itself be repeated; nest a list for that.", parameter);
        }
    }
}
