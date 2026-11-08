using System.Buffers;
using ParquetNeo.Thrift;

namespace ParquetNeo.Metadata;

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
