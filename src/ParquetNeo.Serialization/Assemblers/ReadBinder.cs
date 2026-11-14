using ParquetNeo.Exceptions;
using ParquetNeo.Schema;

namespace ParquetNeo.Serialization.Assemblers;

/// <summary>Matches the row type to the schema of a file as the assemblers walk both, and keeps what they find.</summary>
internal sealed class ReadBinder(int columnCount, int nodeCount)
{
    private readonly int[] _fileColumns = Enumerable.Repeat(-1, columnCount).ToArray();
    private readonly NodeBinding[] _nodes = Enumerable.Repeat(new NodeBinding(-1, 0, 0, 0), nodeCount).ToArray();

    public void BindColumn(int slot, int fileColumn) => _fileColumns[slot] = fileColumn;

    /// <summary>Records what an object, collection or dictionary reads from its node; its controller is the first of its columns that the file has.</summary>
    public void BindNode(int node, int firstColumn, int columnCount, int presentDefinition, int elementsDefinition, int repetitionLevel)
    {
        int controller = -1;
        for (int slot = firstColumn; slot < firstColumn + columnCount; slot++)
        {
            if (_fileColumns[slot] >= 0)
            {
                controller = slot;
                break;
            }
        }

        _nodes[node] = new NodeBinding(controller, presentDefinition, elementsDefinition, repetitionLevel);
    }

    /// <summary>The child of a group by name: the one of the same name or, when there is none and only one differs by case, that one.</summary>
    public static SchemaNode? Find(GroupNode parent, string name)
    {
        var exact = parent.Children.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));
        if (exact is not null)
        {
            return exact;
        }

        var similar = parent.Children.Where(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        return similar.Count == 1 ? similar[0] : null;
    }

    public static SchemaMismatchException Mismatch(string message) => new(message);

    public ReadPlan ToPlan() => new(_fileColumns, _nodes);
}
