using ParquetNeo.Thrift;

namespace ParquetNeo.Metadata;

/// <summary>A row group: the rows of a range of the file, as one column chunk for each column (thrift <c>RowGroup</c>).</summary>
public sealed class RowGroup : IThriftStruct<RowGroup>
{
    /// <summary>The column chunks, in the order of the columns of the schema.</summary>
    public List<ColumnChunk> Columns { get; set; } = new();

    /// <summary>The size of the column chunks before they are compressed, in bytes.</summary>
    public long TotalByteSize { get; set; }

    /// <summary>The number of rows in the row group.</summary>
    public long NumRows { get; set; }

    /// <summary>The columns the rows are sorted by; null when the writer did not say.</summary>
    public List<SortingColumn>? SortingColumns { get; set; }

    /// <summary>Where the first page of the row group is in the file; null when the writer did not say.</summary>
    public long? FileOffset { get; set; }

    /// <summary>The size of the column chunks as they are stored, in bytes; null when the writer did not say.</summary>
    public long? TotalCompressedSize { get; set; }

    /// <summary>The position of the row group in the file; null when the writer did not say.</summary>
    public short? Ordinal { get; set; }

    static RowGroup IThriftStruct<RowGroup>.Read(ref ThriftCompactReader reader)
    {
        var g = new RowGroup();
        reader.BeginStruct();
        while (reader.TryReadField(out short id, out var type))
        {
            switch (id)
            {
                case 1 when type == CompactType.List: g.Columns = ThriftStruct.ReadStructList<ColumnChunk>(ref reader); break;
                case 2 when type == CompactType.I64: g.TotalByteSize = reader.ReadI64(); break;
                case 3 when type == CompactType.I64: g.NumRows = reader.ReadI64(); break;
                case 4 when type == CompactType.List: g.SortingColumns = ThriftStruct.ReadStructList<SortingColumn>(ref reader); break;
                case 5 when type == CompactType.I64: g.FileOffset = reader.ReadI64(); break;
                case 6 when type == CompactType.I64: g.TotalCompressedSize = reader.ReadI64(); break;
                case 7 when type == CompactType.I16: g.Ordinal = reader.ReadI16(); break;
                default: reader.Skip(type); break;
            }
        }

        reader.EndStruct();
        return g;
    }

    void IThriftStruct<RowGroup>.Write(ref ThriftCompactWriter writer)
    {
        writer.BeginStruct();
        ThriftStruct.WriteStructList(ref writer, 1, Columns);
        writer.WriteI64Field(2, TotalByteSize);
        writer.WriteI64Field(3, NumRows);
        if (SortingColumns is not null) ThriftStruct.WriteStructList(ref writer, 4, SortingColumns);
        if (FileOffset is { } fileOffset) writer.WriteI64Field(5, fileOffset);
        if (TotalCompressedSize is { } totalCompressedSize) writer.WriteI64Field(6, totalCompressedSize);
        if (Ordinal is { } ordinal) writer.WriteI16Field(7, ordinal);
        writer.EndStruct();
    }
}