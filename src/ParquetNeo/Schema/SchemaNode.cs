using ParquetNeo.Metadata;

namespace ParquetNeo.Schema;

/// <summary>A node of the schema tree: either a <see cref="GroupNode"/> or a <see cref="PrimitiveNode"/> column.</summary>
/// <remarks>
/// Nodes are built standalone and become part of exactly one <see cref="ParquetSchema"/>, which
/// assigns their parent and levels. Everything else about a node is immutable.
/// </remarks>
public abstract class SchemaNode
{
    private GroupNode? _parent;
    private bool _attached;

    private protected SchemaNode(
        string name,
        FieldRepetitionType repetition,
        LogicalType? logicalType,
        ConvertedType? convertedType,
        int? fieldId)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (!Enum.IsDefined(repetition))
        {
            throw new ArgumentOutOfRangeException(nameof(repetition));
        }

        Name = name;
        Repetition = repetition;
        LogicalType = logicalType;
        ConvertedType = convertedType;
        FieldId = fieldId;
    }

    public string Name { get; }

    /// <summary>Always <see cref="FieldRepetitionType.Required"/> for the schema root, which has no repetition.</summary>
    public FieldRepetitionType Repetition { get; }

    /// <summary>The logical type annotation, or null when there is none or it is not valid for this node.</summary>
    public LogicalType? LogicalType { get; }

    /// <summary>The legacy annotation, written alongside <see cref="LogicalType"/> for older readers.</summary>
    public ConvertedType? ConvertedType { get; }

    public int? FieldId { get; }

    /// <summary>The enclosing group; null for the root, and for a node not yet part of a schema.</summary>
    public GroupNode? Parent => _parent;

    /// <summary>The definition level reached when a value at this node is present (optional and repeated ancestors, including this node).</summary>
    public int MaxDefinitionLevel { get; private set; }

    /// <summary>The number of repeated nodes between the root and this node, including this node.</summary>
    public int MaxRepetitionLevel { get; private set; }

    /// <summary>Names from just below the root down to this node (empty for the root). Computed on demand.</summary>
    public string[] Path
    {
        get
        {
            int depth = 0;
            for (var node = this; node._parent is not null; node = node._parent)
            {
                depth++;
            }

            var path = new string[depth];
            for (var node = this; node._parent is not null; node = node._parent)
            {
                path[--depth] = node.Name;
            }

            return path;
        }
    }

    internal void AttachAsRoot() => MarkAttached();

    internal void Attach(GroupNode parent)
    {
        MarkAttached();
        _parent = parent;
        MaxDefinitionLevel = parent.MaxDefinitionLevel + (Repetition == FieldRepetitionType.Required ? 0 : 1);
        MaxRepetitionLevel = parent.MaxRepetitionLevel + (Repetition == FieldRepetitionType.Repeated ? 1 : 0);
    }

    internal abstract SchemaElement ToElement(bool isRoot);

    private void MarkAttached()
    {
        if (_attached)
        {
            throw new InvalidOperationException($"Schema node '{Name}' already belongs to a schema.");
        }

        _attached = true;
    }

    private protected SchemaElement NewElement(bool isRoot)
    {
        var element = new SchemaElement
        {
            Name = Name,
            RepetitionType = isRoot ? null : Repetition,
            ConvertedType = ConvertedType,
            FieldId = FieldId,
            LogicalType = LogicalType,
        };

        if (LogicalType is { Kind: LogicalTypeKind.Decimal } decimalType)
        {
            // Older readers only look at the SchemaElement fields.
            element.Scale = decimalType.Scale;
            element.Precision = decimalType.Precision;
        }

        return element;
    }
}