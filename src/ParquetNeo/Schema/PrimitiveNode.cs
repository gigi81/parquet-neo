using ParquetNeo.Metadata;

namespace ParquetNeo.Schema;

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