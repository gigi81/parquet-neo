using ParquetNeo.Thrift;

namespace ParquetNeo.Metadata;

public sealed class KeyValue : IThriftStruct<KeyValue>
{
    public string Key { get; set; } = string.Empty;

    public string? Value { get; set; }

    static KeyValue IThriftStruct<KeyValue>.Read(ref ThriftCompactReader reader)
    {
        var kv = new KeyValue();
        reader.BeginStruct();
        while (reader.TryReadField(out short id, out var type))
        {
            switch (id)
            {
                case 1 when type == CompactType.Binary: kv.Key = reader.ReadString(); break;
                case 2 when type == CompactType.Binary: kv.Value = reader.ReadString(); break;
                default: reader.Skip(type); break;
            }
        }

        reader.EndStruct();
        return kv;
    }

    void IThriftStruct<KeyValue>.Write(ref ThriftCompactWriter writer)
    {
        writer.BeginStruct();
        writer.WriteStringField(1, Key);
        if (Value is not null) writer.WriteStringField(2, Value);
        writer.EndStruct();
    }
}