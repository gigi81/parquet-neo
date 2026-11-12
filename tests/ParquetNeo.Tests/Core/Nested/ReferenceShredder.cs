using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Core.Nested;

/// <summary>
/// A deliberately plain implementation of Dremel record shredding, used as the reference in tests: it turns rows into
/// the definition levels, repetition levels and values of every leaf column, following the schema node by node.
/// </summary>
internal static class ReferenceShredder
{
    public static ShreddedColumn[] Shred(ParquetSchema schema, IEnumerable<StructValue> rows)
    {
        var columns = schema.Columns.Select(column => new ShreddedColumn(column)).ToArray();
        var byNode = columns.ToDictionary(column => column.Column);
        foreach (var row in rows)
        {
            foreach (var field in schema.Root.Children)
            {
                ShredNode(field, row.GetValueOrDefault(field.Name), repetition: 0, definition: 0, byNode);
            }
        }

        return columns;
    }

    /// <param name="repetition">The repetition level an entry written for this node starts with: how deep the previous entry of this row shares ancestors with it.</param>
    /// <param name="definition">The number of optional and repeated ancestors that are present.</param>
    private static void ShredNode(SchemaNode node, object? value, int repetition, int definition, Dictionary<PrimitiveNode, ShreddedColumn> sink)
    {
        switch (node.Repetition)
        {
            case FieldRepetitionType.Repeated:
                var items = value as object?[] ?? [];
                if (items.Length == 0)
                {
                    EmitMissing(node, repetition, definition, sink);
                    return;
                }

                for (int i = 0; i < items.Length; i++)
                {
                    // The first element continues where its parent started; the others repeat at this node's level.
                    ShredPresent(node, items[i], i == 0 ? repetition : node.MaxRepetitionLevel, definition + 1, sink);
                }

                return;
            case FieldRepetitionType.Optional:
                if (value is null)
                {
                    EmitMissing(node, repetition, definition, sink);
                    return;
                }

                ShredPresent(node, value, repetition, definition + 1, sink);
                return;
            default:
                ShredPresent(node, value ?? throw new ArgumentException($"The required field '{node.Name}' is null."), repetition, definition, sink);
                return;
        }
    }

    private static void ShredPresent(SchemaNode node, object? value, int repetition, int definition, Dictionary<PrimitiveNode, ShreddedColumn> sink)
    {
        if (node is PrimitiveNode leaf)
        {
            var column = sink[leaf];
            column.Definition.Add(definition);
            column.Repetition.Add(repetition);
            column.Values.Add(value ?? throw new ArgumentException($"The leaf '{node.Name}' has no value."));
            return;
        }

        var fields = value as StructValue ?? throw new ArgumentException($"The group '{node.Name}' needs a {nameof(StructValue)}.");
        foreach (var child in ((GroupNode)node).Children)
        {
            ShredNode(child, fields.GetValueOrDefault(child.Name), repetition, definition, sink);
        }
    }

    /// <summary>Every leaf below <paramref name="node"/> gets one entry that says how far down the value was present.</summary>
    private static void EmitMissing(SchemaNode node, int repetition, int definition, Dictionary<PrimitiveNode, ShreddedColumn> sink)
    {
        if (node is PrimitiveNode leaf)
        {
            sink[leaf].Definition.Add(definition);
            sink[leaf].Repetition.Add(repetition);
            return;
        }

        foreach (var child in ((GroupNode)node).Children)
        {
            EmitMissing(child, repetition, definition, sink);
        }
    }
}
