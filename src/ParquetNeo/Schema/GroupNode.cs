using ParquetNeo.Metadata;

namespace ParquetNeo.Schema;

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