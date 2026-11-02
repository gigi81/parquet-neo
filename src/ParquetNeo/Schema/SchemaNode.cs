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

/// <summary>A leaf of the schema: one stored column.</summary>
public sealed class PrimitiveNode : SchemaNode
{
    /// <summary>Creates a column, deriving the legacy annotation from <paramref name="logicalType"/>.</summary>
    /// <exception cref="ArgumentException">The type length or logical type does not suit <paramref name="type"/>.</exception>
    public PrimitiveNode(
        string name,
        PhysicalType type,
        FieldRepetitionType repetition,
        LogicalType? logicalType = null,
        int? typeLength = null,
        int? fieldId = null)
        : this(name, type, repetition, logicalType, LogicalTypeRules.ToConvertedType(logicalType), typeLength, fieldId, validate: true)
    {
    }

    internal PrimitiveNode(
        string name,
        PhysicalType type,
        FieldRepetitionType repetition,
        LogicalType? logicalType,
        ConvertedType? convertedType,
        int? typeLength,
        int? fieldId,
        bool validate)
        : base(name, repetition, logicalType, convertedType, fieldId)
    {
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type));
        }

        if (type == PhysicalType.FixedLenByteArray)
        {
            if (typeLength is not > 0)
            {
                throw new ArgumentException("FIXED_LEN_BYTE_ARRAY requires a positive type length.", nameof(typeLength));
            }
        }
        else
        {
            typeLength = null;
        }

        if (validate && logicalType is not null
            && LogicalTypeRules.GetError(logicalType, isGroup: false, type, typeLength) is { } error)
        {
            throw new ArgumentException(error, nameof(logicalType));
        }

        Type = type;
        TypeLength = typeLength;
    }

    public PhysicalType Type { get; }

    /// <summary>Byte length of each value of a <see cref="PhysicalType.FixedLenByteArray"/> column; otherwise null.</summary>
    public int? TypeLength { get; }

    /// <summary>Position among the schema's columns, in depth-first order (the order of column chunks in a row group).</summary>
    public int ColumnIndex { get; internal set; }

    internal override SchemaElement ToElement(bool isRoot)
    {
        var element = NewElement(isRoot);
        element.Type = Type;
        element.TypeLength = TypeLength;
        return element;
    }
}

/// <summary>An inner node holding other nodes (a struct, or part of a list or map).</summary>
public sealed class GroupNode : SchemaNode
{
    private readonly SchemaNode[] _children;

    /// <exception cref="ArgumentException">The logical type cannot annotate a group.</exception>
    public GroupNode(
        string name,
        FieldRepetitionType repetition,
        IEnumerable<SchemaNode> children,
        LogicalType? logicalType = null,
        int? fieldId = null)
        : this(name, repetition, children, logicalType, LogicalTypeRules.ToConvertedType(logicalType), fieldId, validate: true)
    {
    }

    internal GroupNode(
        string name,
        FieldRepetitionType repetition,
        IEnumerable<SchemaNode> children,
        LogicalType? logicalType,
        ConvertedType? convertedType,
        int? fieldId,
        bool validate)
        : base(name, repetition, logicalType, convertedType, fieldId)
    {
        ArgumentNullException.ThrowIfNull(children);
        _children = children.ToArray();
        if (_children.Any(c => c is null))
        {
            throw new ArgumentException("Children must not contain null.", nameof(children));
        }

        if (validate && logicalType is not null
            && LogicalTypeRules.GetError(logicalType, isGroup: true, default, null) is { } error)
        {
            throw new ArgumentException(error, nameof(logicalType));
        }
    }

    public IReadOnlyList<SchemaNode> Children => _children;

    public SchemaNode? FindChild(string name) => Array.Find(_children, c => c.Name == name);

    internal override SchemaElement ToElement(bool isRoot)
    {
        var element = NewElement(isRoot);
        element.NumChildren = _children.Length;
        return element;
    }
}
