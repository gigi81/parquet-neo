using ParquetNeo.Metadata;
using ParquetNeo.Schema;

namespace ParquetNeo.Tests.Core.Nested;

/// <summary>Random schemas of required, optional and repeated fields and structs, with random rows to match.</summary>
internal static class RandomNested
{
    public static ParquetSchema Schema(Random random)
    {
        int next = 0;
        return new ParquetSchema(Enumerable.Range(0, random.Next(1, 4)).Select(_ => Node(random, ref next, depth: 0)).ToList());
    }

    private static SchemaNode Node(Random random, ref int next, int depth)
    {
        string name = "f" + next++;
        var repetition = (FieldRepetitionType)random.Next(0, 3);
        if (depth >= 3 || random.Next(3) == 0)
        {
            return new PrimitiveNode(name, PhysicalType.Int32, repetition);
        }

        var children = new List<SchemaNode>();
        for (int i = random.Next(1, 4); i > 0; i--)
        {
            children.Add(Node(random, ref next, depth + 1));
        }

        return new GroupNode(name, repetition, children);
    }

    public static StructValue[] Rows(ParquetSchema schema, Random random, int count) =>
        Enumerable.Range(0, count).Select(_ => Fields(schema.Root, random)).ToArray();

    private static StructValue Fields(GroupNode group, Random random)
    {
        var result = new StructValue();
        foreach (var child in group.Children)
        {
            result[child.Name] = Field(child, random);
        }

        return result;
    }

    private static object? Field(SchemaNode node, Random random) => node.Repetition switch
    {
        FieldRepetitionType.Repeated => random.Next(4) == 0 && random.Next(2) == 0
            ? null // a repeated field with no value reads as an empty list
            : Enumerable.Range(0, random.Next(0, 4)).Select(_ => Single(node, random)).ToArray(),
        FieldRepetitionType.Optional => random.Next(4) == 0 ? null : Single(node, random),
        _ => Single(node, random),
    };

    private static object Single(SchemaNode node, Random random) =>
        node is GroupNode group ? Fields(group, random) : random.Next(-1000, 1000);
}
