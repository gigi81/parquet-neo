using ParquetNeo.Thrift;

namespace ParquetNeo.Metadata.Headers;

/// <summary>The header of a dictionary page (thrift <c>DictionaryPageHeader</c>).</summary>
public sealed class DictionaryPageHeader : IThriftStruct<DictionaryPageHeader>
{
    /// <summary>The number of values in the dictionary.</summary>
    public int NumValues { get; set; }

    /// <summary>The encoding of the values of the dictionary.</summary>
    public ParquetEncoding Encoding { get; set; }

    /// <summary>Whether the values of the dictionary are sorted; null when the writer did not say.</summary>
    public bool? IsSorted { get; set; }

    static DictionaryPageHeader IThriftStruct<DictionaryPageHeader>.Read(ref ThriftCompactReader reader)
    {
        var h = new DictionaryPageHeader();
        reader.BeginStruct();
        while (reader.TryReadField(out short id, out var type))
        {
            switch (id)
            {
                case 1 when type == CompactType.I32: h.NumValues = reader.ReadI32(); break;
                case 2 when type == CompactType.I32: h.Encoding = (ParquetEncoding)reader.ReadI32(); break;
                case 3 when type is CompactType.BoolTrue or CompactType.BoolFalse: h.IsSorted = type == CompactType.BoolTrue; break;
                default: reader.Skip(type); break;
            }
        }

        reader.EndStruct();
        return h;
    }

    void IThriftStruct<DictionaryPageHeader>.Write(ref ThriftCompactWriter writer)
    {
        writer.BeginStruct();
        writer.WriteI32Field(1, NumValues);
        writer.WriteI32Field(2, (int)Encoding);
        if (IsSorted is { } isSorted) writer.WriteBoolField(3, isSorted);
        writer.EndStruct();
    }
}
