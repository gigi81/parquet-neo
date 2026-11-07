using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Nested;

/// <summary>
/// The optional and repeated nodes on the path to a column, with what the level arithmetic needs to know about each.
/// Level <c>j</c> of the plan is present when the definition level is at least <c>j + 1</c>.
/// </summary>
internal sealed class StructurePlan
{
    private StructurePlan(SchemaNode[] nodes, int[] depthBefore, int[] repeatedLevels)
    {
        Nodes = nodes;
        DepthBefore = depthBefore;
        RepeatedLevels = repeatedLevels;
    }

    /// <summary>The optional and repeated nodes from just below the root down to the column, outermost first.</summary>
    public SchemaNode[] Nodes { get; }

    /// <summary>For each level, how many repeated levels come before it: its items are elements of that repeated level (0: rows).</summary>
    public int[] DepthBefore { get; }

    /// <summary>The plan index of each repeated level, by depth minus one: the first repeated node is <c>RepeatedLevels[0]</c>.</summary>
    public int[] RepeatedLevels { get; }

    public int Count => Nodes.Length;

    public bool IsRepeated(int level) => Nodes[level].Repetition == FieldRepetitionType.Repeated;

    /// <summary>The definition level from which the repeated node at <paramref name="depth"/> (1 for the first) has an element.</summary>
    public int DefinitionLevelOfRepeated(int depth) => RepeatedLevels[depth - 1] + 1;

    public static StructurePlan For(PrimitiveNode column)
    {
        var nodes = new List<SchemaNode>();
        for (SchemaNode? node = column; node?.Parent is not null; node = node.Parent)
        {
            if (node.Repetition != FieldRepetitionType.Required)
            {
                nodes.Add(node);
            }
        }

        nodes.Reverse();
        var depthBefore = new int[nodes.Count];
        var repeated = new List<int>();
        for (int level = 0; level < nodes.Count; level++)
        {
            depthBefore[level] = repeated.Count;
            if (nodes[level].Repetition == FieldRepetitionType.Repeated)
            {
                repeated.Add(level);
            }
        }

        return new StructurePlan(nodes.ToArray(), depthBefore, repeated.ToArray());
    }
}
