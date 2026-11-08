using ParquetNeo.Thrift;

namespace ParquetNeo.Metadata;

public sealed class DictionaryPageHeader : IThriftStruct<DictionaryPageHeader>
{
    public int NumValues { get; set; }

    public ParquetEncoding Encoding { get; set; }

    public bool? IsSorted { get; set; }

    static DictionaryPageHeader IThriftStruct<DictionaryPageHeader>.Read(ref ThriftCompactReader reader)
    {
        var h = new DictionaryPageHeader();
        reader.BeginStruct();
        while (reader.TryReadField(out short id, out var type))
        {
            switch (id)
            {
                case 1 when type == CompactType.I32: h.NumValues = reader.ReadI32(); break;
                case 2 when type == CompactType.I32: h.Encoding = (ParquetEncoding)reader.ReadI32(); break;
                case 3 when type is CompactType.BoolTrue or CompactType.BoolFalse: h.IsSorted = type == CompactType.BoolTrue; break;
                default: reader.Skip(type); break;
            }
        }

        reader.EndStruct();
        return h;
    }

    void IThriftStruct<DictionaryPageHeader>.Write(ref ThriftCompactWriter writer)
    {
        writer.BeginStruct();
        writer.WriteI32Field(1, NumValues);
        writer.WriteI32Field(2, (int)Encoding);
        if (IsSorted is { } isSorted) writer.WriteBoolField(3, isSorted);
        writer.EndStruct();
    }
}
