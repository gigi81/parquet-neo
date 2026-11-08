using ParquetNeo.Thrift;

namespace ParquetNeo.Metadata;

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

    static ColumnMetaData IThriftStruct<ColumnMetaData>.Read(ref ThriftCompactReader reader)
    {
        var m = new ColumnMetaData();
        reader.BeginStruct();
        while (reader.TryReadField(out short id, out var type))
        {
            switch (id)
            {
                case 1 when type == CompactType.I32: m.Type = (PhysicalType)reader.ReadI32(); break;
                case 2 when type == CompactType.List: m.Encodings = ThriftStruct.ReadEnumList<ParquetEncoding>(ref reader); break;
                case 3 when type == CompactType.List: m.PathInSchema = ThriftStruct.ReadStringList(ref reader); break;
                case 4 when type == CompactType.I32: m.Codec = (CompressionCodec)reader.ReadI32(); break;
                case 5 when type == CompactType.I64: m.NumValues = reader.ReadI64(); break;
                case 6 when type == CompactType.I64: m.TotalUncompressedSize = reader.ReadI64(); break;
                case 7 when type == CompactType.I64: m.TotalCompressedSize = reader.ReadI64(); break;
                case 8 when type == CompactType.List: m.KeyValueMetadata = ThriftStruct.ReadStructList<KeyValue>(ref reader); break;
                case 9 when type == CompactType.I64: m.DataPageOffset = reader.ReadI64(); break;
                case 10 when type == CompactType.I64: m.IndexPageOffset = reader.ReadI64(); break;
                case 11 when type == CompactType.I64: m.DictionaryPageOffset = reader.ReadI64(); break;
                case 12 when type == CompactType.Struct: m.Statistics = ThriftStruct.ReadStruct<Statistics>(ref reader); break;
                case 13 when type == CompactType.List: m.EncodingStats = ThriftStruct.ReadStructList<PageEncodingStats>(ref reader); break;
                case 14 when type == CompactType.I64: m.BloomFilterOffset = reader.ReadI64(); break;
                case 15 when type == CompactType.I32: m.BloomFilterLength = reader.ReadI32(); break;
                case 16 when type == CompactType.Struct: m.SizeStatistics = ThriftStruct.ReadStruct<SizeStatistics>(ref reader); break;
                default: reader.Skip(type); break;
            }
        }

        reader.EndStruct();
        return m;
    }

    void IThriftStruct<ColumnMetaData>.Write(ref ThriftCompactWriter writer)
    {
        writer.BeginStruct();
        writer.WriteI32Field(1, (int)Type);
        ThriftStruct.WriteEnumList(ref writer, 2, Encodings);
        ThriftStruct.WriteStringList(ref writer, 3, PathInSchema);
        writer.WriteI32Field(4, (int)Codec);
        writer.WriteI64Field(5, NumValues);
        writer.WriteI64Field(6, TotalUncompressedSize);
        writer.WriteI64Field(7, TotalCompressedSize);
        if (KeyValueMetadata is not null) ThriftStruct.WriteStructList(ref writer, 8, KeyValueMetadata);
        writer.WriteI64Field(9, DataPageOffset);
        if (IndexPageOffset is { } indexPageOffset) writer.WriteI64Field(10, indexPageOffset);
        if (DictionaryPageOffset is { } dictionaryPageOffset) writer.WriteI64Field(11, dictionaryPageOffset);
        if (Statistics is not null) ThriftStruct.WriteStructField(ref writer, 12, Statistics);
        if (EncodingStats is not null) ThriftStruct.WriteStructList(ref writer, 13, EncodingStats);
        if (BloomFilterOffset is { } bloomFilterOffset) writer.WriteI64Field(14, bloomFilterOffset);
        if (BloomFilterLength is { } bloomFilterLength) writer.WriteI32Field(15, bloomFilterLength);
        if (SizeStatistics is not null) ThriftStruct.WriteStructField(ref writer, 16, SizeStatistics);
        writer.EndStruct();
    }
}