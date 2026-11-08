using ParquetNeo.Thrift;

namespace ParquetNeo.Metadata;

/// <summary>How many pages of a type used an encoding (thrift <c>PageEncodingStats</c>).</summary>
public sealed class PageEncodingStats : IThriftStruct<PageEncodingStats>
{
    public PageType PageType { get; set; }

    public ParquetEncoding Encoding { get; set; }

    public int Count { get; set; }

    static PageEncodingStats IThriftStruct<PageEncodingStats>.Read(ref ThriftCompactReader reader)
    {
        var s = new PageEncodingStats();
        reader.BeginStruct();
        while (reader.TryReadField(out short id, out var type))
        {
            switch (id)
            {
                case 1 when type == CompactType.I32: s.PageType = (PageType)reader.ReadI32(); break;
                case 2 when type == CompactType.I32: s.Encoding = (ParquetEncoding)reader.ReadI32(); break;
                case 3 when type == CompactType.I32: s.Count = reader.ReadI32(); break;
                default: reader.Skip(type); break;
            }
        }

        reader.EndStruct();
        return s;
    }

    void IThriftStruct<PageEncodingStats>.Write(ref ThriftCompactWriter writer)
    {
        writer.BeginStruct();
        writer.WriteI32Field(1, (int)PageType);
        writer.WriteI32Field(2, (int)Encoding);
        writer.WriteI32Field(3, Count);
        writer.EndStruct();
    }
}