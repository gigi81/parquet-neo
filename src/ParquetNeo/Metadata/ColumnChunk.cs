using ParquetNeo.Thrift;

namespace ParquetNeo.Metadata;

/// <summary>Location and metadata of one column chunk (thrift <c>ColumnChunk</c>). Encryption fields are skipped.</summary>
public sealed class ColumnChunk : IThriftStruct<ColumnChunk>
{
    public string? FilePath { get; set; }

    public long FileOffset { get; set; }

    public ColumnMetaData? MetaData { get; set; }

    public long? OffsetIndexOffset { get; set; }

    public int? OffsetIndexLength { get; set; }

    public long? ColumnIndexOffset { get; set; }

    public int? ColumnIndexLength { get; set; }

    static ColumnChunk IThriftStruct<ColumnChunk>.Read(ref ThriftCompactReader reader)
    {
        var c = new ColumnChunk();
        reader.BeginStruct();
        while (reader.TryReadField(out short id, out var type))
        {
            switch (id)
            {
                case 1 when type == CompactType.Binary: c.FilePath = reader.ReadString(); break;
                case 2 when type == CompactType.I64: c.FileOffset = reader.ReadI64(); break;
                case 3 when type == CompactType.Struct: c.MetaData = ThriftStruct.ReadStruct<ColumnMetaData>(ref reader); break;
                case 4 when type == CompactType.I64: c.OffsetIndexOffset = reader.ReadI64(); break;
                case 5 when type == CompactType.I32: c.OffsetIndexLength = reader.ReadI32(); break;
                case 6 when type == CompactType.I64: c.ColumnIndexOffset = reader.ReadI64(); break;
                case 7 when type == CompactType.I32: c.ColumnIndexLength = reader.ReadI32(); break;
                default: reader.Skip(type); break;
            }
        }

        reader.EndStruct();
        return c;
    }

    void IThriftStruct<ColumnChunk>.Write(ref ThriftCompactWriter writer)
    {
        writer.BeginStruct();
        if (FilePath is not null) writer.WriteStringField(1, FilePath);
        writer.WriteI64Field(2, FileOffset);
        if (MetaData is not null) ThriftStruct.WriteStructField(ref writer, 3, MetaData);
        if (OffsetIndexOffset is { } offsetIndexOffset) writer.WriteI64Field(4, offsetIndexOffset);
        if (OffsetIndexLength is { } offsetIndexLength) writer.WriteI32Field(5, offsetIndexLength);
        if (ColumnIndexOffset is { } columnIndexOffset) writer.WriteI64Field(6, columnIndexOffset);
        if (ColumnIndexLength is { } columnIndexLength) writer.WriteI32Field(7, columnIndexLength);
        writer.EndStruct();
    }
}