using ParquetNeo.Thrift;

namespace ParquetNeo.Metadata;

public sealed class KeyValue : IThriftStruct<KeyValue>
{
    public string Key { get; set; } = string.Empty;

    public string? Value { get; set; }

    static KeyValue IThriftStruct<KeyValue>.Read(ref ThriftCompactReader r)
    {
        var kv = new KeyValue();
        r.BeginStruct();
        while (r.TryReadField(out short id, out var type))
        {
            switch (id)
            {
                case 1 when type == CompactType.Binary: kv.Key = r.ReadString(); break;
                case 2 when type == CompactType.Binary: kv.Value = r.ReadString(); break;
                default: r.Skip(type); break;
            }
        }

        r.EndStruct();
        return kv;
    }

    void IThriftStruct<KeyValue>.Write(ref ThriftCompactWriter w)
    {
        w.BeginStruct();
        w.WriteStringField(1, Key);
        if (Value is not null) w.WriteStringField(2, Value);
        w.EndStruct();
    }
}

/// <summary>A column a row group is sorted by (thrift <c>SortingColumn</c>).</summary>
public sealed class SortingColumn : IThriftStruct<SortingColumn>
{
    public int ColumnIndex { get; set; }

    public bool Descending { get; set; }

    public bool NullsFirst { get; set; }

    static SortingColumn IThriftStruct<SortingColumn>.Read(ref ThriftCompactReader r)
    {
        var c = new SortingColumn();
        r.BeginStruct();
        while (r.TryReadField(out short id, out var type))
        {
            switch (id)
            {
                case 1 when type == CompactType.I32: c.ColumnIndex = r.ReadI32(); break;
                case 2 when type is CompactType.BoolTrue or CompactType.BoolFalse: c.Descending = type == CompactType.BoolTrue; break;
                case 3 when type is CompactType.BoolTrue or CompactType.BoolFalse: c.NullsFirst = type == CompactType.BoolTrue; break;
                default: r.Skip(type); break;
            }
        }

        r.EndStruct();
        return c;
    }

    void IThriftStruct<SortingColumn>.Write(ref ThriftCompactWriter w)
    {
        w.BeginStruct();
        w.WriteI32Field(1, ColumnIndex);
        w.WriteBoolField(2, Descending);
        w.WriteBoolField(3, NullsFirst);
        w.EndStruct();
    }
}

/// <summary>How many pages of a type used an encoding (thrift <c>PageEncodingStats</c>).</summary>
public sealed class PageEncodingStats : IThriftStruct<PageEncodingStats>
{
    public PageType PageType { get; set; }

    public ParquetEncoding Encoding { get; set; }

    public int Count { get; set; }

    static PageEncodingStats IThriftStruct<PageEncodingStats>.Read(ref ThriftCompactReader r)
    {
        var s = new PageEncodingStats();
        r.BeginStruct();
        while (r.TryReadField(out short id, out var type))
        {
            switch (id)
            {
                case 1 when type == CompactType.I32: s.PageType = (PageType)r.ReadI32(); break;
                case 2 when type == CompactType.I32: s.Encoding = (ParquetEncoding)r.ReadI32(); break;
                case 3 when type == CompactType.I32: s.Count = r.ReadI32(); break;
                default: r.Skip(type); break;
            }
        }

        r.EndStruct();
        return s;
    }

    void IThriftStruct<PageEncodingStats>.Write(ref ThriftCompactWriter w)
    {
        w.BeginStruct();
        w.WriteI32Field(1, (int)PageType);
        w.WriteI32Field(2, (int)Encoding);
        w.WriteI32Field(3, Count);
        w.EndStruct();
    }
}

/// <summary>Metadata of one column chunk (thrift <c>ColumnMetaData</c>). Geospatial statistics are not read yet.</summary>
public sealed class ColumnMetaData : IThriftStruct<ColumnMetaData>
{
    public PhysicalType Type { get; set; }

    public List<ParquetEncoding> Encodings { get; set; } = new();

    public List<string> PathInSchema { get; set; } = new();

    public CompressionCodec Codec { get; set; }

    public long NumValues { get; set; }

    public long TotalUncompressedSize { get; set; }

    public long TotalCompressedSize { get; set; }

    public List<KeyValue>? KeyValueMetadata { get; set; }

    public long DataPageOffset { get; set; }

    public long? IndexPageOffset { get; set; }

    public long? DictionaryPageOffset { get; set; }

    public Statistics? Statistics { get; set; }

    public List<PageEncodingStats>? EncodingStats { get; set; }

    public long? BloomFilterOffset { get; set; }

    public int? BloomFilterLength { get; set; }

    public SizeStatistics? SizeStatistics { get; set; }

    static ColumnMetaData IThriftStruct<ColumnMetaData>.Read(ref ThriftCompactReader r)
    {
        var m = new ColumnMetaData();
        r.BeginStruct();
        while (r.TryReadField(out short id, out var type))
        {
            switch (id)
            {
                case 1 when type == CompactType.I32: m.Type = (PhysicalType)r.ReadI32(); break;
                case 2 when type == CompactType.List: m.Encodings = ThriftStruct.ReadEnumList<ParquetEncoding>(ref r); break;
                case 3 when type == CompactType.List: m.PathInSchema = ThriftStruct.ReadStringList(ref r); break;
                case 4 when type == CompactType.I32: m.Codec = (CompressionCodec)r.ReadI32(); break;
                case 5 when type == CompactType.I64: m.NumValues = r.ReadI64(); break;
                case 6 when type == CompactType.I64: m.TotalUncompressedSize = r.ReadI64(); break;
                case 7 when type == CompactType.I64: m.TotalCompressedSize = r.ReadI64(); break;
                case 8 when type == CompactType.List: m.KeyValueMetadata = ThriftStruct.ReadStructList<KeyValue>(ref r); break;
                case 9 when type == CompactType.I64: m.DataPageOffset = r.ReadI64(); break;
                case 10 when type == CompactType.I64: m.IndexPageOffset = r.ReadI64(); break;
                case 11 when type == CompactType.I64: m.DictionaryPageOffset = r.ReadI64(); break;
                case 12 when type == CompactType.Struct: m.Statistics = ThriftStruct.ReadStruct<Statistics>(ref r); break;
                case 13 when type == CompactType.List: m.EncodingStats = ThriftStruct.ReadStructList<PageEncodingStats>(ref r); break;
                case 14 when type == CompactType.I64: m.BloomFilterOffset = r.ReadI64(); break;
                case 15 when type == CompactType.I32: m.BloomFilterLength = r.ReadI32(); break;
                case 16 when type == CompactType.Struct: m.SizeStatistics = ThriftStruct.ReadStruct<SizeStatistics>(ref r); break;
                default: r.Skip(type); break;
            }
        }

        r.EndStruct();
        return m;
    }

    void IThriftStruct<ColumnMetaData>.Write(ref ThriftCompactWriter w)
    {
        w.BeginStruct();
        w.WriteI32Field(1, (int)Type);
        ThriftStruct.WriteEnumList(ref w, 2, Encodings);
        ThriftStruct.WriteStringList(ref w, 3, PathInSchema);
        w.WriteI32Field(4, (int)Codec);
        w.WriteI64Field(5, NumValues);
        w.WriteI64Field(6, TotalUncompressedSize);
        w.WriteI64Field(7, TotalCompressedSize);
        if (KeyValueMetadata is not null) ThriftStruct.WriteStructList(ref w, 8, KeyValueMetadata);
        w.WriteI64Field(9, DataPageOffset);
        if (IndexPageOffset is { } indexPageOffset) w.WriteI64Field(10, indexPageOffset);
        if (DictionaryPageOffset is { } dictionaryPageOffset) w.WriteI64Field(11, dictionaryPageOffset);
        if (Statistics is not null) ThriftStruct.WriteStructField(ref w, 12, Statistics);
        if (EncodingStats is not null) ThriftStruct.WriteStructList(ref w, 13, EncodingStats);
        if (BloomFilterOffset is { } bloomFilterOffset) w.WriteI64Field(14, bloomFilterOffset);
        if (BloomFilterLength is { } bloomFilterLength) w.WriteI32Field(15, bloomFilterLength);
        if (SizeStatistics is not null) ThriftStruct.WriteStructField(ref w, 16, SizeStatistics);
        w.EndStruct();
    }
}
