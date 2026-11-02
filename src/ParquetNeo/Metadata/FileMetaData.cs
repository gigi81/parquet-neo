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

    static ColumnChunk IThriftStruct<ColumnChunk>.Read(ref ThriftCompactReader r)
    {
        var c = new ColumnChunk();
        r.BeginStruct();
        while (r.TryReadField(out short id, out var type))
        {
            switch (id)
            {
                case 1 when type == CompactType.Binary: c.FilePath = r.ReadString(); break;
                case 2 when type == CompactType.I64: c.FileOffset = r.ReadI64(); break;
                case 3 when type == CompactType.Struct: c.MetaData = ThriftStruct.ReadStruct<ColumnMetaData>(ref r); break;
                case 4 when type == CompactType.I64: c.OffsetIndexOffset = r.ReadI64(); break;
                case 5 when type == CompactType.I32: c.OffsetIndexLength = r.ReadI32(); break;
                case 6 when type == CompactType.I64: c.ColumnIndexOffset = r.ReadI64(); break;
                case 7 when type == CompactType.I32: c.ColumnIndexLength = r.ReadI32(); break;
                default: r.Skip(type); break;
            }
        }

        r.EndStruct();
        return c;
    }

    void IThriftStruct<ColumnChunk>.Write(ref ThriftCompactWriter w)
    {
        w.BeginStruct();
        if (FilePath is not null) w.WriteStringField(1, FilePath);
        w.WriteI64Field(2, FileOffset);
        if (MetaData is not null) ThriftStruct.WriteStructField(ref w, 3, MetaData);
        if (OffsetIndexOffset is { } offsetIndexOffset) w.WriteI64Field(4, offsetIndexOffset);
        if (OffsetIndexLength is { } offsetIndexLength) w.WriteI32Field(5, offsetIndexLength);
        if (ColumnIndexOffset is { } columnIndexOffset) w.WriteI64Field(6, columnIndexOffset);
        if (ColumnIndexLength is { } columnIndexLength) w.WriteI32Field(7, columnIndexLength);
        w.EndStruct();
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

    static RowGroup IThriftStruct<RowGroup>.Read(ref ThriftCompactReader r)
    {
        var g = new RowGroup();
        r.BeginStruct();
        while (r.TryReadField(out short id, out var type))
        {
            switch (id)
            {
                case 1 when type == CompactType.List: g.Columns = ThriftStruct.ReadStructList<ColumnChunk>(ref r); break;
                case 2 when type == CompactType.I64: g.TotalByteSize = r.ReadI64(); break;
                case 3 when type == CompactType.I64: g.NumRows = r.ReadI64(); break;
                case 4 when type == CompactType.List: g.SortingColumns = ThriftStruct.ReadStructList<SortingColumn>(ref r); break;
                case 5 when type == CompactType.I64: g.FileOffset = r.ReadI64(); break;
                case 6 when type == CompactType.I64: g.TotalCompressedSize = r.ReadI64(); break;
                case 7 when type == CompactType.I16: g.Ordinal = r.ReadI16(); break;
                default: r.Skip(type); break;
            }
        }

        r.EndStruct();
        return g;
    }

    void IThriftStruct<RowGroup>.Write(ref ThriftCompactWriter w)
    {
        w.BeginStruct();
        ThriftStruct.WriteStructList(ref w, 1, Columns);
        w.WriteI64Field(2, TotalByteSize);
        w.WriteI64Field(3, NumRows);
        if (SortingColumns is not null) ThriftStruct.WriteStructList(ref w, 4, SortingColumns);
        if (FileOffset is { } fileOffset) w.WriteI64Field(5, fileOffset);
        if (TotalCompressedSize is { } totalCompressedSize) w.WriteI64Field(6, totalCompressedSize);
        if (Ordinal is { } ordinal) w.WriteI16Field(7, ordinal);
        w.EndStruct();
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

    static FileMetaData IThriftStruct<FileMetaData>.Read(ref ThriftCompactReader r)
    {
        var m = new FileMetaData();
        r.BeginStruct();
        while (r.TryReadField(out short id, out var type))
        {
            switch (id)
            {
                case 1 when type == CompactType.I32: m.Version = r.ReadI32(); break;
                case 2 when type == CompactType.List: m.Schema = ThriftStruct.ReadStructList<SchemaElement>(ref r); break;
                case 3 when type == CompactType.I64: m.NumRows = r.ReadI64(); break;
                case 4 when type == CompactType.List: m.RowGroups = ThriftStruct.ReadStructList<RowGroup>(ref r); break;
                case 5 when type == CompactType.List: m.KeyValueMetadata = ThriftStruct.ReadStructList<KeyValue>(ref r); break;
                case 6 when type == CompactType.Binary: m.CreatedBy = r.ReadString(); break;
                case 7 when type == CompactType.List: m.ColumnOrders = ReadColumnOrders(ref r); break;
                default: r.Skip(type); break;
            }
        }

        r.EndStruct();
        return m;
    }

    void IThriftStruct<FileMetaData>.Write(ref ThriftCompactWriter w)
    {
        w.BeginStruct();
        w.WriteI32Field(1, Version);
        ThriftStruct.WriteStructList(ref w, 2, Schema);
        w.WriteI64Field(3, NumRows);
        ThriftStruct.WriteStructList(ref w, 4, RowGroups);
        if (KeyValueMetadata is not null) ThriftStruct.WriteStructList(ref w, 5, KeyValueMetadata);
        if (CreatedBy is not null) w.WriteStringField(6, CreatedBy);
        if (ColumnOrders is not null) WriteColumnOrders(ref w, ColumnOrders);
        w.EndStruct();
    }

    private static List<ColumnOrder> ReadColumnOrders(ref ThriftCompactReader r)
    {
        int count = r.ReadListHeader(CompactType.Struct);
        var orders = new List<ColumnOrder>(count);
        for (int i = 0; i < count; i++)
        {
            var order = ColumnOrder.Unrecognized;
            r.BeginStruct();
            while (r.TryReadField(out short id, out var type))
            {
                if (id is >= 1 and <= 3 && type == CompactType.Struct)
                {
                    order = (ColumnOrder)id;
                }

                r.Skip(type);
            }

            r.EndStruct();
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
