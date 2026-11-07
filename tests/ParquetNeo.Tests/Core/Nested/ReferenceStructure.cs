using ParquetNeo.Metadata;
using ParquetNeo.Nested;
using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Core.Nested;

/// <summary>
/// Builds the <see cref="ColumnStructure"/> of a column straight from the rows, walking the schema along the column's path,
/// without any levels. It is the second reference next to <see cref="ReferenceShredder"/>: the two have nothing in common, so
/// comparing their results checks the conversion between levels and structure.
/// </summary>
internal static class ReferenceStructure
{
    public static ColumnStructure Build(PrimitiveNode column, IEnumerable<StructValue> rows)
    {
        var path = new List<SchemaNode>();
        for (SchemaNode? node = column; node?.Parent is not null; node = node.Parent)
        {
            path.Add(node);
        }

        path.Reverse();
        var validity = new Dictionary<SchemaNode, List<bool>>();
        var counts = new Dictionary<SchemaNode, List<int>>();
        foreach (var node in path)
        {
            if (node.Repetition == FieldRepetitionType.Optional)
            {
                validity[node] = new List<bool>();
            }
            else if (node.Repetition == FieldRepetitionType.Repeated)
            {
                counts[node] = new List<int>();
            }
        }

        foreach (var row in rows)
        {
            Walk(path, 0, row.GetValueOrDefault(path[0].Name), present: true, validity, counts);
        }

        var levels = new List<StructureLevel>();
        foreach (var node in path)
        {
            if (node.Repetition == FieldRepetitionType.Optional)
            {
                levels.Add(StructureLevel.ForOptional(node, validity[node].ToArray()));
            }
            else if (node.Repetition == FieldRepetitionType.Repeated)
            {
                var offsets = new int[counts[node].Count + 1];
                for (int i = 0; i < counts[node].Count; i++)
                {
                    offsets[i + 1] = offsets[i] + counts[node][i];
                }

                levels.Add(StructureLevel.ForRepeated(node, offsets));
            }
        }

        return ColumnStructure.Create(column, levels);
    }

    /// <param name="present">Whether every ancestor of the node holds a value; below a missing one only the entries of the nodes at the same depth are recorded.</param>
    private static void Walk(List<SchemaNode> path, int index, object? value, bool present, Dictionary<SchemaNode, List<bool>> validity, Dictionary<SchemaNode, List<int>> counts)
    {
        var node = path[index];
        bool last = index == path.Count - 1;
        switch (node.Repetition)
        {
            case FieldRepetitionType.Repeated:
                var items = present ? value as object?[] ?? [] : [];
                counts[node].Add(items.Length);
                if (!last)
                {
                    foreach (var item in items)
                    {
                        Walk(path, index + 1, ChildValue(item, path[index + 1]), present: true, validity, counts);
                    }
                }

                break;
            case FieldRepetitionType.Optional:
                bool valid = present && value is not null;
                validity[node].Add(valid);
                if (!last)
                {
                    Walk(path, index + 1, ChildValue(value, path[index + 1]), valid, validity, counts);
                }

                break;
            default:
                if (!last)
                {
                    Walk(path, index + 1, ChildValue(value, path[index + 1]), present, validity, counts);
                }

                break;
        }
    }

    private static object? ChildValue(object? parent, SchemaNode child) => (parent as StructValue)?.GetValueOrDefault(child.Name);
}
