using System.Buffers;
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

public sealed class RowGroup : IThriftStruct<RowGroup>
{
    public List<ColumnChunk> Columns { get; set; } = new();

    public long TotalByteSize { get; set; }

    public long NumRows { get; set; }

    public List<SortingColumn>? SortingColumns { get; set; }

    public long? FileOffset { get; set; }

    public long? TotalCompressedSize { get; set; }

    public short? Ordinal { get; set; }

    static RowGroup IThriftStruct<RowGroup>.Read(ref ThriftCompactReader reader)
    {
        var g = new RowGroup();
        reader.BeginStruct();
        while (reader.TryReadField(out short id, out var type))
        {
            switch (id)
            {
                case 1 when type == CompactType.List: g.Columns = ThriftStruct.ReadStructList<ColumnChunk>(ref reader); break;
                case 2 when type == CompactType.I64: g.TotalByteSize = reader.ReadI64(); break;
                case 3 when type == CompactType.I64: g.NumRows = reader.ReadI64(); break;
                case 4 when type == CompactType.List: g.SortingColumns = ThriftStruct.ReadStructList<SortingColumn>(ref reader); break;
                case 5 when type == CompactType.I64: g.FileOffset = reader.ReadI64(); break;
                case 6 when type == CompactType.I64: g.TotalCompressedSize = reader.ReadI64(); break;
                case 7 when type == CompactType.I16: g.Ordinal = reader.ReadI16(); break;
                default: reader.Skip(type); break;
            }
        }

        reader.EndStruct();
        return g;
    }

    void IThriftStruct<RowGroup>.Write(ref ThriftCompactWriter writer)
    {
        writer.BeginStruct();
        ThriftStruct.WriteStructList(ref writer, 1, Columns);
        writer.WriteI64Field(2, TotalByteSize);
        writer.WriteI64Field(3, NumRows);
        if (SortingColumns is not null) ThriftStruct.WriteStructList(ref writer, 4, SortingColumns);
        if (FileOffset is { } fileOffset) writer.WriteI64Field(5, fileOffset);
        if (TotalCompressedSize is { } totalCompressedSize) writer.WriteI64Field(6, totalCompressedSize);
        if (Ordinal is { } ordinal) writer.WriteI16Field(7, ordinal);
        writer.EndStruct();
    }
}

/// <summary>
/// The Parquet footer (thrift <c>FileMetaData</c>). Unknown fields, including the extension field 32767,
/// are skipped when reading; encryption fields are not supported yet.
/// </summary>
public sealed class FileMetaData : IThriftStruct<FileMetaData>
{
    public int Version { get; set; }

    /// <summary>The schema as a flattened depth-first list; the first element is the root.</summary>
    public List<SchemaElement> Schema { get; set; } = new();

    public long NumRows { get; set; }

    public List<RowGroup> RowGroups { get; set; } = new();

    public List<KeyValue>? KeyValueMetadata { get; set; }

    public string? CreatedBy { get; set; }

    public List<ColumnOrder>? ColumnOrders { get; set; }

    /// <summary>Parses the Thrift-encoded footer. Bytes after the end of the struct are ignored.</summary>
    public static FileMetaData Deserialize(ReadOnlySpan<byte> data) =>
        ThriftStruct.Deserialize<FileMetaData>(data, out _);

    public void Serialize(IBufferWriter<byte> output) => ThriftStruct.Serialize(this, output);

    static FileMetaData IThriftStruct<FileMetaData>.Read(ref ThriftCompactReader reader)
    {
        var m = new FileMetaData();
        reader.BeginStruct();
        while (reader.TryReadField(out short id, out var type))
        {
            switch (id)
            {
                case 1 when type == CompactType.I32: m.Version = reader.ReadI32(); break;
                case 2 when type == CompactType.List: m.Schema = ThriftStruct.ReadStructList<SchemaElement>(ref reader); break;
                case 3 when type == CompactType.I64: m.NumRows = reader.ReadI64(); break;
                case 4 when type == CompactType.List: m.RowGroups = ThriftStruct.ReadStructList<RowGroup>(ref reader); break;
                case 5 when type == CompactType.List: m.KeyValueMetadata = ThriftStruct.ReadStructList<KeyValue>(ref reader); break;
                case 6 when type == CompactType.Binary: m.CreatedBy = reader.ReadString(); break;
                case 7 when type == CompactType.List: m.ColumnOrders = ReadColumnOrders(ref reader); break;
                default: reader.Skip(type); break;
            }
        }

        reader.EndStruct();
        return m;
    }

    void IThriftStruct<FileMetaData>.Write(ref ThriftCompactWriter writer)
    {
        writer.BeginStruct();
        writer.WriteI32Field(1, Version);
        ThriftStruct.WriteStructList(ref writer, 2, Schema);
        writer.WriteI64Field(3, NumRows);
        ThriftStruct.WriteStructList(ref writer, 4, RowGroups);
        if (KeyValueMetadata is not null) ThriftStruct.WriteStructList(ref writer, 5, KeyValueMetadata);
        if (CreatedBy is not null) writer.WriteStringField(6, CreatedBy);
        if (ColumnOrders is not null) WriteColumnOrders(ref writer, ColumnOrders);
        writer.EndStruct();
    }

    private static List<ColumnOrder> ReadColumnOrders(ref ThriftCompactReader reader)
    {
        int count = reader.ReadListHeader(CompactType.Struct);
        var orders = new List<ColumnOrder>(count);
        for (int i = 0; i < count; i++)
        {
            var order = ColumnOrder.Unrecognized;
            reader.BeginStruct();
            while (reader.TryReadField(out short id, out var type))
            {
                if (id is >= 1 and <= 3 && type == CompactType.Struct)
                {
                    order = (ColumnOrder)id;
                }

                reader.Skip(type);
            }

            reader.EndStruct();
            orders.Add(order);
        }

        return orders;
    }

    private static void WriteColumnOrders(ref ThriftCompactWriter w, List<ColumnOrder> orders)
    {
        w.BeginListField(7, CompactType.Struct, orders.Count);
        foreach (var order in orders)
        {
            w.BeginStruct();
            if (order != ColumnOrder.Unrecognized)
            {
                w.BeginStructField((short)order);
                w.EndStruct();
            }

            w.EndStruct();
        }
    }
}
