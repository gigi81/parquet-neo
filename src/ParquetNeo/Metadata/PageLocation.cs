using ParquetNeo.Thrift;

namespace ParquetNeo.Metadata;

/// <summary>Where a data page of a column chunk is, and which row it starts with (thrift <c>PageLocation</c>).</summary>
public sealed class PageLocation : IThriftStruct<PageLocation>
{
    /// <summary>The position of the page header in the file.</summary>
    public long Offset { get; set; }

    /// <summary>The size of the page with its header.</summary>
    public int CompressedPageSize { get; set; }

    /// <summary>The position in the row group of the first row of the page. A page starts with a row, never in the middle of one.</summary>
    public long FirstRowIndex { get; set; }

    static PageLocation IThriftStruct<PageLocation>.Read(ref ThriftCompactReader reader)
    {
        var location = new PageLocation();
        reader.BeginStruct();
        while (reader.TryReadField(out short id, out var type))
        {
            switch (id)
            {
                case 1 when type == CompactType.I64: location.Offset = reader.ReadI64(); break;
                case 2 when type == CompactType.I32: location.CompressedPageSize = reader.ReadI32(); break;
                case 3 when type == CompactType.I64: location.FirstRowIndex = reader.ReadI64(); break;
                default: reader.Skip(type); break;
            }
        }

        reader.EndStruct();
        return location;
    }

    void IThriftStruct<PageLocation>.Write(ref ThriftCompactWriter writer)
    {
        writer.BeginStruct();
        writer.WriteI64Field(1, Offset);
        writer.WriteI32Field(2, CompressedPageSize);
        writer.WriteI64Field(3, FirstRowIndex);
        writer.EndStruct();
    }
}
