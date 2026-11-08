using ParquetNeo.Thrift;

namespace ParquetNeo.Metadata;

public sealed class DataPageHeader : IThriftStruct<DataPageHeader>
{
    public int NumValues { get; set; }

    public ParquetEncoding Encoding { get; set; }

    public ParquetEncoding DefinitionLevelEncoding { get; set; }

    public ParquetEncoding RepetitionLevelEncoding { get; set; }

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
