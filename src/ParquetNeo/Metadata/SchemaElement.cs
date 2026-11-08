using ParquetNeo.Thrift;

namespace ParquetNeo.Metadata;

/// <summary>One node of the flattened, depth-first schema tree (thrift <c>SchemaElement</c>).</summary>
public sealed class SchemaElement : IThriftStruct<SchemaElement>
{
    /// <summary>Physical type; absent for group nodes.</summary>
    public PhysicalType? Type { get; set; }

    /// <summary>Byte length of <see cref="PhysicalType.FixedLenByteArray"/> values.</summary>
    public int? TypeLength { get; set; }

    /// <summary>Absent for the schema root.</summary>
    public FieldRepetitionType? RepetitionType { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>Number of children; present for group nodes.</summary>
    public int? NumChildren { get; set; }

    public ConvertedType? ConvertedType { get; set; }

    public int? Scale { get; set; }

    public int? Precision { get; set; }

    public int? FieldId { get; set; }

    /// <summary>Null when absent, or when the file uses a logical type this library does not know.</summary>
    public LogicalType? LogicalType { get; set; }

    static SchemaElement IThriftStruct<SchemaElement>.Read(ref ThriftCompactReader reader)
    {
        var e = new SchemaElement();
        reader.BeginStruct();
        while (reader.TryReadField(out short id, out var type))
        {
            switch (id)
            {
                case 1 when type == CompactType.I32: e.Type = (PhysicalType)reader.ReadI32(); break;
                case 2 when type == CompactType.I32: e.TypeLength = reader.ReadI32(); break;
                case 3 when type == CompactType.I32: e.RepetitionType = (FieldRepetitionType)reader.ReadI32(); break;
                case 4 when type == CompactType.Binary: e.Name = reader.ReadString(); break;
                case 5 when type == CompactType.I32: e.NumChildren = reader.ReadI32(); break;
                case 6 when type == CompactType.I32: e.ConvertedType = (ConvertedType)reader.ReadI32(); break;
                case 7 when type == CompactType.I32: e.Scale = reader.ReadI32(); break;
                case 8 when type == CompactType.I32: e.Precision = reader.ReadI32(); break;
                case 9 when type == CompactType.I32: e.FieldId = reader.ReadI32(); break;
                case 10 when type == CompactType.Struct:
                    var logicalType = Metadata.LogicalType.Read(ref reader);
                    e.LogicalType = logicalType.Kind == LogicalTypeKind.Unrecognized ? null : logicalType;
                    break;
                default: reader.Skip(type); break;
            }
        }

        reader.EndStruct();
        return e;
    }

    void IThriftStruct<SchemaElement>.Write(ref ThriftCompactWriter writer)
    {
        writer.BeginStruct();
        if (Type is { } physicalType) writer.WriteI32Field(1, (int)physicalType);
        if (TypeLength is { } typeLength) writer.WriteI32Field(2, typeLength);
        if (RepetitionType is { } repetition) writer.WriteI32Field(3, (int)repetition);
        writer.WriteStringField(4, Name);
        if (NumChildren is { } numChildren) writer.WriteI32Field(5, numChildren);
        if (ConvertedType is { } convertedType) writer.WriteI32Field(6, (int)convertedType);
        if (Scale is { } scale) writer.WriteI32Field(7, scale);
        if (Precision is { } precision) writer.WriteI32Field(8, precision);
        if (FieldId is { } fieldId) writer.WriteI32Field(9, fieldId);
        if (LogicalType is { Kind: not LogicalTypeKind.Unrecognized } logicalType)
        {
            writer.BeginStructField(10);
            // The union is written as a field value, so reuse its struct framing.
            logicalType.WriteMembers(ref writer);
            writer.EndStruct();
        }

        writer.EndStruct();
    }
}
