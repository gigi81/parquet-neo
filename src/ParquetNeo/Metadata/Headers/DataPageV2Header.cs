using ParquetNeo.Thrift;

namespace ParquetNeo.Metadata.Headers;

public sealed class DataPageV2Header : IThriftStruct<DataPageV2Header>
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

    static DataPageV2Header IThriftStruct<DataPageV2Header>.Read(ref ThriftCompactReader reader)
    {
        var h = new DataPageV2Header();
        reader.BeginStruct();
        while (reader.TryReadField(out short id, out var type))
        {
            switch (id)
            {
                case 1 when type == CompactType.I32: h.NumValues = reader.ReadI32(); break;
                case 2 when type == CompactType.I32: h.NumNulls = reader.ReadI32(); break;
                case 3 when type == CompactType.I32: h.NumRows = reader.ReadI32(); break;
                case 4 when type == CompactType.I32: h.Encoding = (ParquetEncoding)reader.ReadI32(); break;
                case 5 when type == CompactType.I32: h.DefinitionLevelsByteLength = reader.ReadI32(); break;
                case 6 when type == CompactType.I32: h.RepetitionLevelsByteLength = reader.ReadI32(); break;
                case 7 when type is CompactType.BoolTrue or CompactType.BoolFalse: h.IsCompressed = type == CompactType.BoolTrue; break;
                case 8 when type == CompactType.Struct: h.Statistics = ThriftStruct.ReadStruct<Statistics>(ref reader); break;
                default: reader.Skip(type); break;
            }
        }

        reader.EndStruct();
        return h;
    }

    void IThriftStruct<DataPageV2Header>.Write(ref ThriftCompactWriter writer)
    {
        writer.BeginStruct();
        writer.WriteI32Field(1, NumValues);
        writer.WriteI32Field(2, NumNulls);
        writer.WriteI32Field(3, NumRows);
        writer.WriteI32Field(4, (int)Encoding);
        writer.WriteI32Field(5, DefinitionLevelsByteLength);
        writer.WriteI32Field(6, RepetitionLevelsByteLength);
        writer.WriteBoolField(7, IsCompressed);
        if (Statistics is not null) ThriftStruct.WriteStructField(ref writer, 8, Statistics);
        writer.EndStruct();
    }
}
