using ParquetNeo.Exceptions;
using ParquetNeo.Metadata;

namespace ParquetNeo.Schema;

/// <summary>
/// The schema of a Parquet file as a tree, with the definition and repetition levels of every node
/// and the leaf <see cref="Columns"/> in the order their chunks are stored.
/// </summary>
public sealed class ParquetSchema
{
    private readonly PrimitiveNode[] _columns;
    private Dictionary<string, PrimitiveNode>? _columnsByPath;

    /// <summary>Creates a schema whose top-level fields are <paramref name="fields"/>.</summary>
    /// <param name="fields">Nodes that do not yet belong to another schema.</param>
    /// <param name="name">Name of the root group; it is not part of any column path.</param>
    public ParquetSchema(IEnumerable<SchemaNode> fields, string name = "schema")
        : this(new GroupNode(name, FieldRepetitionType.Required, fields, null, null, null, validate: true))
    {
    }

    private ParquetSchema(GroupNode root)
    {
        Root = root;
        root.AttachAsRoot();

        // Iterative pre-order walk: schemas read from files can be arbitrarily deep.
        var columns = new List<PrimitiveNode>();
        var pending = new Stack<SchemaNode>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (node is PrimitiveNode column)
            {
                column.ColumnIndex = columns.Count;
                columns.Add(column);
            }
            else
            {
                var group = (GroupNode)node;
                for (int i = group.Children.Count - 1; i >= 0; i--)
                {
                    var child = group.Children[i];
                    child.Attach(group);
                    pending.Push(child);
                }
            }
        }

        _columns = columns.ToArray();
    }

    public GroupNode Root { get; }

    /// <summary>The leaf columns, in depth-first order.</summary>
    public IReadOnlyList<PrimitiveNode> Columns => _columns;

    /// <summary>Finds a column by its path from just below the root, e.g. <c>["address", "city"]</c>.</summary>
    public PrimitiveNode? FindColumn(IReadOnlyList<string> path)
    {
        _columnsByPath ??= _columns.ToDictionary(c => PathKey(c.Path));
        return _columnsByPath.GetValueOrDefault(PathKey(path));
    }

    /// <summary>Flattens the tree into the depth-first <see cref="SchemaElement"/> list stored in the footer.</summary>
    public List<SchemaElement> ToElements()
    {
        var elements = new List<SchemaElement>();
        var pending = new Stack<SchemaNode>();
        pending.Push(Root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            elements.Add(node.ToElement(isRoot: ReferenceEquals(node, Root)));
            if (node is GroupNode group)
            {
                for (int i = group.Children.Count - 1; i >= 0; i--)
                {
                    pending.Push(group.Children[i]);
                }
            }
        }

        return elements;
    }

    /// <summary>Builds the tree from the footer's flattened schema.</summary>
    /// <exception cref="InvalidSchemaException">The element list is not a well-formed depth-first tree.</exception>
    /// <remarks>
    /// A logical type that does not suit its column (or that this library does not know) is ignored,
    /// as the spec requires, leaving the physical type to describe the data.
    /// </remarks>
    public static ParquetSchema FromElements(IReadOnlyList<SchemaElement> elements)
    {
        ArgumentNullException.ThrowIfNull(elements);
        if (elements.Count == 0)
        {
            throw new InvalidSchemaException("The schema has no elements; the root is missing.");
        }

        var root = elements[0];
        if (root.Type is not null)
        {
            throw new InvalidSchemaException("The schema root must be a group, but it has a physical type.");
        }

        var open = new Stack<OpenGroup>();
        open.Push(new OpenGroup(root, 0, GetChildCount(root, 0), isRoot: true));
        int index = 0;
        GroupNode? result = null;

        while (result is null)
        {
            var top = open.Peek();
            if (top.Remaining == 0)
            {
                open.Pop();
                var group = top.ToNode();
                if (open.Count == 0)
                {
                    result = group;
                }
                else
                {
                    open.Peek().Add(group);
                }

                continue;
            }

            if (++index >= elements.Count)
            {
                throw new InvalidSchemaException("The schema ends before all declared children were found.");
            }

            var element = elements[index];
            if (element.Type is null)
            {
                open.Push(new OpenGroup(element, index, GetChildCount(element, index), isRoot: false));
            }
            else
            {
                top.Add(CreateColumn(element, index));
            }
        }

        if (index != elements.Count - 1)
        {
            throw new InvalidSchemaException(
                $"The schema has {elements.Count - 1 - index} element(s) that are not part of the tree.");
        }

        return new ParquetSchema(result);
    }

    private static int GetChildCount(SchemaElement element, int index)
    {
        int count = element.NumChildren ?? 0;
        if (count < 0)
        {
            throw new InvalidSchemaException($"Schema element {index} ('{element.Name}') has a negative child count.");
        }

        return count;
    }

    private static FieldRepetitionType GetRepetition(SchemaElement element, int index, bool isRoot)
    {
        if (isRoot)
        {
            return FieldRepetitionType.Required;
        }

        if (element.RepetitionType is not { } repetition || !Enum.IsDefined(repetition))
        {
            throw new InvalidSchemaException(
                $"Schema element {index} ('{element.Name}') has a missing or unknown repetition type.");
        }

        return repetition;
    }

    private static PrimitiveNode CreateColumn(SchemaElement element, int index)
    {
        var type = element.Type!.Value;
        if (!Enum.IsDefined(type))
        {
            throw new InvalidSchemaException($"Schema element {index} ('{element.Name}') has an unknown physical type {(int)type}.");
        }

        if (element.NumChildren is > 0)
        {
            throw new InvalidSchemaException($"Schema element {index} ('{element.Name}') has a physical type and children.");
        }

        if (type == PhysicalType.FixedLenByteArray && element.TypeLength is not > 0)
        {
            throw new InvalidSchemaException(
                $"Schema element {index} ('{element.Name}') is FIXED_LEN_BYTE_ARRAY without a valid type length.");
        }

        var repetition = GetRepetition(element, index, isRoot: false);
        int? typeLength = type == PhysicalType.FixedLenByteArray ? element.TypeLength : null;
        var logicalType = ResolveLogicalType(element, isGroup: false, type, typeLength);
        return new PrimitiveNode(element.Name, type, repetition, logicalType, element.ConvertedType, typeLength, element.FieldId, validate: false);
    }

    private static LogicalType? ResolveLogicalType(SchemaElement element, bool isGroup, PhysicalType type, int? typeLength)
    {
        var candidate = element.LogicalType
                        ?? LogicalTypeRules.FromConvertedType(element.ConvertedType, element.Scale, element.Precision);
        return candidate is not null && LogicalTypeRules.GetError(candidate, isGroup, type, typeLength) is null
            ? candidate
            : null;
    }

    private static string PathKey(IReadOnlyList<string> path) => string.Join('\0', path);

    /// <summary>A group whose children are still being collected.</summary>
    private sealed class OpenGroup
    {
        private readonly SchemaElement _element;
        private readonly int _index;
        private readonly bool _isRoot;
        private readonly List<SchemaNode> _children = new();

        public OpenGroup(SchemaElement element, int index, int childCount, bool isRoot)
        {
            _element = element;
            _index = index;
            _isRoot = isRoot;
            Remaining = childCount;
        }

        public int Remaining { get; private set; }

        public void Add(SchemaNode child)
        {
            _children.Add(child);
            Remaining--;
        }

        public GroupNode ToNode()
        {
            var repetition = GetRepetition(_element, _index, _isRoot);
            var logicalType = ResolveLogicalType(_element, isGroup: true, default, null);
            return new GroupNode(_element.Name, repetition, _children, logicalType, _element.ConvertedType, _element.FieldId, validate: false);
        }
    }
}
