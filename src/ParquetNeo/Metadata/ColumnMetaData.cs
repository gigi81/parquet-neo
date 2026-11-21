using ParquetNeo.Thrift;

namespace ParquetNeo.Metadata;

/// <summary>Metadata of one column chunk (thrift <c>ColumnMetaData</c>). Geospatial statistics are not read yet.</summary>
public sealed class ColumnMetaData : IThriftStruct<ColumnMetaData>
{
    /// <summary>The physical type of the values.</summary>
    public PhysicalType Type { get; set; }

    /// <summary>The encodings the pages of the column chunk use.</summary>
    public List<ParquetEncoding> Encodings { get; set; } = new();

    /// <summary>The names from the root of the schema down to the column.</summary>
    public List<string> PathInSchema { get; set; } = new();

    /// <summary>The compression codec of the pages.</summary>
    public CompressionCodec Codec { get; set; }

    /// <summary>The number of values in the column chunk, nulls included; for a nested column more than the rows.</summary>
    public long NumValues { get; set; }

    /// <summary>The size of the pages, with their headers, before they are compressed, in bytes.</summary>
    public long TotalUncompressedSize { get; set; }

    /// <summary>The size of the pages, with their headers, as they are stored, in bytes.</summary>
    public long TotalCompressedSize { get; set; }

    /// <summary>Free key and value pairs of the column chunk; null when there are none.</summary>
    public List<KeyValue>? KeyValueMetadata { get; set; }

    /// <summary>Where the first data page is in the file.</summary>
    public long DataPageOffset { get; set; }

    /// <summary>Where the index page is in the file; null when there is none, which is the usual case.</summary>
    public long? IndexPageOffset { get; set; }

    /// <summary>Where the dictionary page is in the file; null or zero when the column chunk has no dictionary.</summary>
    public long? DictionaryPageOffset { get; set; }

    /// <summary>The smallest and largest value of the column chunk and how many are null; null when the writer did not write them.</summary>
    public Statistics? Statistics { get; set; }

    /// <summary>How many pages of each type use each encoding; null when the writer did not write them.</summary>
    public List<PageEncodingStats>? EncodingStats { get; set; }

    /// <summary>Where the bloom filter of the column chunk is in the file; null when it has none.</summary>
    public long? BloomFilterOffset { get; set; }

    /// <summary>The size of the bloom filter with its header, in bytes; null when the writer did not say.</summary>
    public int? BloomFilterLength { get; set; }

    /// <summary>The sizes of the values and the histograms of the levels; null when the writer did not write them.</summary>
    public SizeStatistics? SizeStatistics { get; set; }

    static ColumnMetaData IThriftStruct<ColumnMetaData>.Read(ref ThriftCompactReader reader)
    {
        var m = new ColumnMetaData();
        reader.BeginStruct();

        while (reader.TryReadField(out var id, out var type))
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

        if (KeyValueMetadata is not null)
            ThriftStruct.WriteStructList(ref writer, 8, KeyValueMetadata);

        writer.WriteI64Field(9, DataPageOffset);

        if (IndexPageOffset is { } indexPageOffset)
            writer.WriteI64Field(10, indexPageOffset);

        if (DictionaryPageOffset is { } dictionaryPageOffset)
            writer.WriteI64Field(11, dictionaryPageOffset);

        if (Statistics is not null)
            ThriftStruct.WriteStructField(ref writer, 12, Statistics);

        if (EncodingStats is not null)
            ThriftStruct.WriteStructList(ref writer, 13, EncodingStats);

        if (BloomFilterOffset is { } bloomFilterOffset)
            writer.WriteI64Field(14, bloomFilterOffset);

        if (BloomFilterLength is { } bloomFilterLength)
            writer.WriteI32Field(15, bloomFilterLength);

        if (SizeStatistics is not null)
            ThriftStruct.WriteStructField(ref writer, 16, SizeStatistics);

        writer.EndStruct();
    }
}
