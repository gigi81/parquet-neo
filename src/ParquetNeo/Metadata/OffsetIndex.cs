using System.Buffers;
using ParquetNeo.Thrift;

namespace ParquetNeo.Metadata;

/// <summary>The positions of the data pages of a column chunk (thrift <c>OffsetIndex</c>), so that a reader can go to a page without reading the ones before it.</summary>
public sealed class OffsetIndex : IThriftStruct<OffsetIndex>
{
    /// <summary>The data pages in the order of the file; the first rows of the pages increase.</summary>
    public List<PageLocation> PageLocations { get; set; } = new();

    public static OffsetIndex Deserialize(ReadOnlySpan<byte> data) => ThriftStruct.Deserialize<OffsetIndex>(data, out _);

    public void Serialize(IBufferWriter<byte> output) => ThriftStruct.Serialize(this, output);

    static OffsetIndex IThriftStruct<OffsetIndex>.Read(ref ThriftCompactReader reader)
    {
        var index = new OffsetIndex();
        reader.BeginStruct();
        while (reader.TryReadField(out short id, out var type))
        {
            switch (id)
            {
                case 1 when type == CompactType.List: index.PageLocations = ThriftStruct.ReadStructList<PageLocation>(ref reader); break;
                default: reader.Skip(type); break;
            }
        }

        reader.EndStruct();
        return index;
    }

    void IThriftStruct<OffsetIndex>.Write(ref ThriftCompactWriter writer)
    {
        writer.BeginStruct();
        ThriftStruct.WriteStructList(ref writer, 1, PageLocations);
        writer.EndStruct();
    }
}
