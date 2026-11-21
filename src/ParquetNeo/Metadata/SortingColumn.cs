using ParquetNeo.Thrift;

namespace ParquetNeo.Metadata;

/// <summary>A column a row group is sorted by (thrift <c>SortingColumn</c>).</summary>
public sealed class SortingColumn : IThriftStruct<SortingColumn>
{
    /// <summary>The position of the column in the schema.</summary>
    public int ColumnIndex { get; set; }

    /// <summary>Whether the rows are sorted from the largest value to the smallest.</summary>
    public bool Descending { get; set; }

    /// <summary>Whether the nulls come before the other values.</summary>
    public bool NullsFirst { get; set; }

    static SortingColumn IThriftStruct<SortingColumn>.Read(ref ThriftCompactReader reader)
    {
        var c = new SortingColumn();
        reader.BeginStruct();
        while (reader.TryReadField(out short id, out var type))
        {
            switch (id)
            {
                case 1 when type == CompactType.I32: c.ColumnIndex = reader.ReadI32(); break;
                case 2 when type is CompactType.BoolTrue or CompactType.BoolFalse: c.Descending = type == CompactType.BoolTrue; break;
                case 3 when type is CompactType.BoolTrue or CompactType.BoolFalse: c.NullsFirst = type == CompactType.BoolTrue; break;
                default: reader.Skip(type); break;
            }
        }

        reader.EndStruct();
        return c;
    }

    void IThriftStruct<SortingColumn>.Write(ref ThriftCompactWriter writer)
    {
        writer.BeginStruct();
        writer.WriteI32Field(1, ColumnIndex);
        writer.WriteBoolField(2, Descending);
        writer.WriteBoolField(3, NullsFirst);
        writer.EndStruct();
    }
}