using System.Buffers;
using ParquetNeo.Thrift;

namespace ParquetNeo.Metadata;

/// <summary>Header preceding every page in a column chunk (thrift <c>PageHeader</c>).</summary>
public sealed class PageHeader : IThriftStruct<PageHeader>
{
    public PageType Type { get; set; }

    public int UncompressedPageSize { get; set; }

    public int CompressedPageSize { get; set; }

    /// <summary>CRC32 of the page data (after the header), when present.</summary>
    public int? Crc { get; set; }

    public DataPageHeader? DataPageHeader { get; set; }

    public DictionaryPageHeader? DictionaryPageHeader { get; set; }

    public DataPageHeaderV2? DataPageHeaderV2 { get; set; }

    /// <summary>Parses a page header at the start of <paramref name="data"/>.</summary>
    /// <param name="bytesConsumed">The number of bytes the header occupies; the page data follows.</param>
    public static PageHeader Deserialize(ReadOnlySpan<byte> data, out int bytesConsumed) =>
        ThriftStruct.Deserialize<PageHeader>(data, out bytesConsumed);

    public void Serialize(IBufferWriter<byte> output) => ThriftStruct.Serialize(this, output);

    static PageHeader IThriftStruct<PageHeader>.Read(ref ThriftCompactReader reader)
    {
        var h = new PageHeader();
        reader.BeginStruct();
        while (reader.TryReadField(out short id, out var type))
        {
            switch (id)
            {
                case 1 when type == CompactType.I32: h.Type = (PageType)reader.ReadI32(); break;
                case 2 when type == CompactType.I32: h.UncompressedPageSize = reader.ReadI32(); break;
                case 3 when type == CompactType.I32: h.CompressedPageSize = reader.ReadI32(); break;
                case 4 when type == CompactType.I32: h.Crc = reader.ReadI32(); break;
                case 5 when type == CompactType.Struct: h.DataPageHeader = ThriftStruct.ReadStruct<DataPageHeader>(ref reader); break;
                case 7 when type == CompactType.Struct: h.DictionaryPageHeader = ThriftStruct.ReadStruct<DictionaryPageHeader>(ref reader); break;
                case 8 when type == CompactType.Struct: h.DataPageHeaderV2 = ThriftStruct.ReadStruct<DataPageHeaderV2>(ref reader); break;
                default: reader.Skip(type); break; // includes the empty index_page_header (6)
            }
        }

        reader.EndStruct();
        return h;
    }

    void IThriftStruct<PageHeader>.Write(ref ThriftCompactWriter writer)
    {
        writer.BeginStruct();
        writer.WriteI32Field(1, (int)Type);
        writer.WriteI32Field(2, UncompressedPageSize);
        writer.WriteI32Field(3, CompressedPageSize);
        if (Crc is { } crc) writer.WriteI32Field(4, crc);
        if (DataPageHeader is not null) ThriftStruct.WriteStructField(ref writer, 5, DataPageHeader);
        if (DictionaryPageHeader is not null) ThriftStruct.WriteStructField(ref writer, 7, DictionaryPageHeader);
        if (DataPageHeaderV2 is not null) ThriftStruct.WriteStructField(ref writer, 8, DataPageHeaderV2);
        writer.EndStruct();
    }
}
