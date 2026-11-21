using ParquetNeo.Thrift;

namespace ParquetNeo.Metadata.Headers;

/// <summary>The header of a data page of version 1 (thrift <c>DataPageHeader</c>).</summary>
public sealed class DataPageHeader : IThriftStruct<DataPageHeader>
{
    /// <summary>The number of values in the page, nulls included.</summary>
    public int NumValues { get; set; }

    /// <summary>The encoding of the values.</summary>
    public ParquetEncoding Encoding { get; set; }

    /// <summary>The encoding of the definition levels.</summary>
    public ParquetEncoding DefinitionLevelEncoding { get; set; }

    /// <summary>The encoding of the repetition levels.</summary>
    public ParquetEncoding RepetitionLevelEncoding { get; set; }

    /// <summary>The statistics of the values of the page; null when the writer did not write them.</summary>
    public Statistics? Statistics { get; set; }

    static DataPageHeader IThriftStruct<DataPageHeader>.Read(ref ThriftCompactReader reader)
    {
        var h = new DataPageHeader();
        reader.BeginStruct();
        while (reader.TryReadField(out short id, out var type))
        {
            switch (id)
            {
                case 1 when type == CompactType.I32: h.NumValues = reader.ReadI32(); break;
                case 2 when type == CompactType.I32: h.Encoding = (ParquetEncoding)reader.ReadI32(); break;
                case 3 when type == CompactType.I32: h.DefinitionLevelEncoding = (ParquetEncoding)reader.ReadI32(); break;
                case 4 when type == CompactType.I32: h.RepetitionLevelEncoding = (ParquetEncoding)reader.ReadI32(); break;
                case 5 when type == CompactType.Struct: h.Statistics = ThriftStruct.ReadStruct<Statistics>(ref reader); break;
                default: reader.Skip(type); break;
            }
        }

        reader.EndStruct();
        return h;
    }

    void IThriftStruct<DataPageHeader>.Write(ref ThriftCompactWriter writer)
    {
        writer.BeginStruct();
        writer.WriteI32Field(1, NumValues);
        writer.WriteI32Field(2, (int)Encoding);
        writer.WriteI32Field(3, (int)DefinitionLevelEncoding);
        writer.WriteI32Field(4, (int)RepetitionLevelEncoding);
        if (Statistics is not null) ThriftStruct.WriteStructField(ref writer, 5, Statistics);
        writer.EndStruct();
    }
}
