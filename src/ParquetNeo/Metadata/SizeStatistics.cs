using ParquetNeo.Thrift;

namespace ParquetNeo.Metadata;

/// <summary>Size statistics of a column chunk (thrift <c>SizeStatistics</c>).</summary>
public sealed class SizeStatistics : IThriftStruct<SizeStatistics>
{
    public long? UnencodedByteArrayDataBytes { get; set; }

    public List<long>? RepetitionLevelHistogram { get; set; }

    public List<long>? DefinitionLevelHistogram { get; set; }

    static SizeStatistics IThriftStruct<SizeStatistics>.Read(ref ThriftCompactReader reader)
    {
        var s = new SizeStatistics();
        reader.BeginStruct();
        while (reader.TryReadField(out short id, out var type))
        {
            switch (id)
            {
                case 1 when type == CompactType.I64: s.UnencodedByteArrayDataBytes = reader.ReadI64(); break;
                case 2 when type == CompactType.List: s.RepetitionLevelHistogram = ThriftStruct.ReadI64List(ref reader); break;
                case 3 when type == CompactType.List: s.DefinitionLevelHistogram = ThriftStruct.ReadI64List(ref reader); break;
                default: reader.Skip(type); break;
            }
        }

        reader.EndStruct();
        return s;
    }

    void IThriftStruct<SizeStatistics>.Write(ref ThriftCompactWriter writer)
    {
        writer.BeginStruct();
        if (UnencodedByteArrayDataBytes is { } bytes) writer.WriteI64Field(1, bytes);
        if (RepetitionLevelHistogram is not null) ThriftStruct.WriteI64List(ref writer, 2, RepetitionLevelHistogram);
        if (DefinitionLevelHistogram is not null) ThriftStruct.WriteI64List(ref writer, 3, DefinitionLevelHistogram);
        writer.EndStruct();
    }
}