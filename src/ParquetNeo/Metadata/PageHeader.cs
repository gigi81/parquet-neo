using System.Buffers;
using ParquetNeo.Thrift;

namespace ParquetNeo.Metadata;

public sealed class DataPageHeader : IThriftStruct<DataPageHeader>
{
    public int NumValues { get; set; }

    public ParquetEncoding Encoding { get; set; }

    public ParquetEncoding DefinitionLevelEncoding { get; set; }

    public ParquetEncoding RepetitionLevelEncoding { get; set; }

    public Statistics? Statistics { get; set; }

    static DataPageHeader IThriftStruct<DataPageHeader>.Read(ref ThriftCompactReader r)
    {
        var h = new DataPageHeader();
        r.BeginStruct();
        while (r.TryReadField(out short id, out var type))
        {
            switch (id)
            {
                case 1 when type == CompactType.I32: h.NumValues = r.ReadI32(); break;
                case 2 when type == CompactType.I32: h.Encoding = (ParquetEncoding)r.ReadI32(); break;
                case 3 when type == CompactType.I32: h.DefinitionLevelEncoding = (ParquetEncoding)r.ReadI32(); break;
                case 4 when type == CompactType.I32: h.RepetitionLevelEncoding = (ParquetEncoding)r.ReadI32(); break;
                case 5 when type == CompactType.Struct: h.Statistics = ThriftStruct.ReadStruct<Statistics>(ref r); break;
                default: r.Skip(type); break;
            }
        }

        r.EndStruct();
        return h;
    }

    void IThriftStruct<DataPageHeader>.Write(ref ThriftCompactWriter w)
    {
        w.BeginStruct();
        w.WriteI32Field(1, NumValues);
        w.WriteI32Field(2, (int)Encoding);
        w.WriteI32Field(3, (int)DefinitionLevelEncoding);
        w.WriteI32Field(4, (int)RepetitionLevelEncoding);
        if (Statistics is not null) ThriftStruct.WriteStructField(ref w, 5, Statistics);
        w.EndStruct();
    }
}

public sealed class DictionaryPageHeader : IThriftStruct<DictionaryPageHeader>
{
    public int NumValues { get; set; }

    public ParquetEncoding Encoding { get; set; }

    public bool? IsSorted { get; set; }

    static DictionaryPageHeader IThriftStruct<DictionaryPageHeader>.Read(ref ThriftCompactReader r)
    {
        var h = new DictionaryPageHeader();
        r.BeginStruct();
        while (r.TryReadField(out short id, out var type))
        {
            switch (id)
            {
                case 1 when type == CompactType.I32: h.NumValues = r.ReadI32(); break;
                case 2 when type == CompactType.I32: h.Encoding = (ParquetEncoding)r.ReadI32(); break;
                case 3 when type is CompactType.BoolTrue or CompactType.BoolFalse: h.IsSorted = type == CompactType.BoolTrue; break;
                default: r.Skip(type); break;
            }
        }

        r.EndStruct();
        return h;
    }

    void IThriftStruct<DictionaryPageHeader>.Write(ref ThriftCompactWriter w)
    {
        w.BeginStruct();
        w.WriteI32Field(1, NumValues);
        w.WriteI32Field(2, (int)Encoding);
        if (IsSorted is { } isSorted) w.WriteBoolField(3, isSorted);
        w.EndStruct();
    }
}

public sealed class DataPageHeaderV2 : IThriftStruct<DataPageHeaderV2>
{
    public int NumValues { get; set; }

    public int NumNulls { get; set; }

    public int NumRows { get; set; }

    public ParquetEncoding Encoding { get; set; }

    public int DefinitionLevelsByteLength { get; set; }

    public int RepetitionLevelsByteLength { get; set; }

    /// <summary>Whether the values section is compressed (levels never are). Defaults to true when absent.</summary>
    public bool IsCompressed { get; set; } = true;

    public Statistics? Statistics { get; set; }

    static DataPageHeaderV2 IThriftStruct<DataPageHeaderV2>.Read(ref ThriftCompactReader r)
    {
        var h = new DataPageHeaderV2();
        r.BeginStruct();
        while (r.TryReadField(out short id, out var type))
        {
            switch (id)
            {
                case 1 when type == CompactType.I32: h.NumValues = r.ReadI32(); break;
                case 2 when type == CompactType.I32: h.NumNulls = r.ReadI32(); break;
                case 3 when type == CompactType.I32: h.NumRows = r.ReadI32(); break;
                case 4 when type == CompactType.I32: h.Encoding = (ParquetEncoding)r.ReadI32(); break;
                case 5 when type == CompactType.I32: h.DefinitionLevelsByteLength = r.ReadI32(); break;
                case 6 when type == CompactType.I32: h.RepetitionLevelsByteLength = r.ReadI32(); break;
                case 7 when type is CompactType.BoolTrue or CompactType.BoolFalse: h.IsCompressed = type == CompactType.BoolTrue; break;
                case 8 when type == CompactType.Struct: h.Statistics = ThriftStruct.ReadStruct<Statistics>(ref r); break;
                default: r.Skip(type); break;
            }
        }

        r.EndStruct();
        return h;
    }

    void IThriftStruct<DataPageHeaderV2>.Write(ref ThriftCompactWriter w)
    {
        w.BeginStruct();
        w.WriteI32Field(1, NumValues);
        w.WriteI32Field(2, NumNulls);
        w.WriteI32Field(3, NumRows);
        w.WriteI32Field(4, (int)Encoding);
        w.WriteI32Field(5, DefinitionLevelsByteLength);
        w.WriteI32Field(6, RepetitionLevelsByteLength);
        w.WriteBoolField(7, IsCompressed);
        if (Statistics is not null) ThriftStruct.WriteStructField(ref w, 8, Statistics);
        w.EndStruct();
    }
}

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

    static PageHeader IThriftStruct<PageHeader>.Read(ref ThriftCompactReader r)
    {
        var h = new PageHeader();
        r.BeginStruct();
        while (r.TryReadField(out short id, out var type))
        {
            switch (id)
            {
                case 1 when type == CompactType.I32: h.Type = (PageType)r.ReadI32(); break;
                case 2 when type == CompactType.I32: h.UncompressedPageSize = r.ReadI32(); break;
                case 3 when type == CompactType.I32: h.CompressedPageSize = r.ReadI32(); break;
                case 4 when type == CompactType.I32: h.Crc = r.ReadI32(); break;
                case 5 when type == CompactType.Struct: h.DataPageHeader = ThriftStruct.ReadStruct<DataPageHeader>(ref r); break;
                case 7 when type == CompactType.Struct: h.DictionaryPageHeader = ThriftStruct.ReadStruct<DictionaryPageHeader>(ref r); break;
                case 8 when type == CompactType.Struct: h.DataPageHeaderV2 = ThriftStruct.ReadStruct<DataPageHeaderV2>(ref r); break;
                default: r.Skip(type); break; // includes the empty index_page_header (6)
            }
        }

        r.EndStruct();
        return h;
    }

    void IThriftStruct<PageHeader>.Write(ref ThriftCompactWriter w)
    {
        w.BeginStruct();
        w.WriteI32Field(1, (int)Type);
        w.WriteI32Field(2, UncompressedPageSize);
        w.WriteI32Field(3, CompressedPageSize);
        if (Crc is { } crc) w.WriteI32Field(4, crc);
        if (DataPageHeader is not null) ThriftStruct.WriteStructField(ref w, 5, DataPageHeader);
        if (DictionaryPageHeader is not null) ThriftStruct.WriteStructField(ref w, 7, DictionaryPageHeader);
        if (DataPageHeaderV2 is not null) ThriftStruct.WriteStructField(ref w, 8, DataPageHeaderV2);
        w.EndStruct();
    }
}
