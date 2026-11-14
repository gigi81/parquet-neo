using System.Buffers;
using ParquetNeo.Thrift;

namespace ParquetNeo.Metadata;

/// <summary>
/// The smallest and largest value of every data page of a column chunk, and how many are null (thrift <c>ColumnIndex</c>); entry <c>i</c> of
/// each list is for the page at <c>i</c> in the <see cref="OffsetIndex"/>. The bounds are encoded as the statistics of the column are.
/// </summary>
public sealed class ColumnIndex : IThriftStruct<ColumnIndex>
{
    /// <summary>Whether a page holds only nulls, in which case its bounds are empty.</summary>
    public List<bool> NullPages { get; set; } = new();

    public List<byte[]> MinValues { get; set; } = new();

    public List<byte[]> MaxValues { get; set; } = new();

    /// <summary>The order of the bounds as the writer states it: 0 unordered, 1 ascending, 2 descending. Readers here do not use it.</summary>
    public int BoundaryOrder { get; set; }

    /// <summary>How many values of each page are null; absent when the writer did not say.</summary>
    public List<long>? NullCounts { get; set; }

    public static ColumnIndex Deserialize(ReadOnlySpan<byte> data) => ThriftStruct.Deserialize<ColumnIndex>(data, out _);

    public void Serialize(IBufferWriter<byte> output) => ThriftStruct.Serialize(this, output);

    static ColumnIndex IThriftStruct<ColumnIndex>.Read(ref ThriftCompactReader reader)
    {
        var index = new ColumnIndex();
        reader.BeginStruct();
        while (reader.TryReadField(out short id, out var type))
        {
            switch (id)
            {
                case 1 when type == CompactType.List: index.NullPages = ReadBooleans(ref reader); break;
                case 2 when type == CompactType.List: index.MinValues = ReadBinaries(ref reader); break;
                case 3 when type == CompactType.List: index.MaxValues = ReadBinaries(ref reader); break;
                case 4 when type == CompactType.I32: index.BoundaryOrder = reader.ReadI32(); break;
                case 5 when type == CompactType.List: index.NullCounts = ThriftStruct.ReadI64List(ref reader); break;
                default: reader.Skip(type); break;
            }
        }

        reader.EndStruct();
        return index;
    }

    void IThriftStruct<ColumnIndex>.Write(ref ThriftCompactWriter writer)
    {
        writer.BeginStruct();
        writer.BeginListField(1, CompactType.BoolTrue, NullPages.Count);
        foreach (bool nullPage in NullPages)
        {
            writer.WriteBoolElement(nullPage);
        }

        WriteBinaries(ref writer, 2, MinValues);
        WriteBinaries(ref writer, 3, MaxValues);
        writer.WriteI32Field(4, BoundaryOrder);
        if (NullCounts is not null)
        {
            ThriftStruct.WriteI64List(ref writer, 5, NullCounts);
        }

        writer.EndStruct();
    }

    private static List<bool> ReadBooleans(ref ThriftCompactReader reader)
    {
        int count = reader.ReadListHeader(CompactType.BoolTrue);
        var list = new List<bool>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(reader.ReadBoolElement());
        }

        return list;
    }

    private static List<byte[]> ReadBinaries(ref ThriftCompactReader reader)
    {
        int count = reader.ReadListHeader(CompactType.Binary);
        var list = new List<byte[]>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(reader.ReadBytes());
        }

        return list;
    }

    private static void WriteBinaries(ref ThriftCompactWriter writer, short id, List<byte[]> items)
    {
        writer.BeginListField(id, CompactType.Binary, items.Count);
        foreach (byte[] item in items)
        {
            writer.WriteBinary(item);
        }
    }
}
