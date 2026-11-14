using System.Buffers;
using ParquetNeo.Thrift;

namespace ParquetNeo.Metadata.Headers;

/// <summary>The header in front of the bits of a bloom filter (thrift <c>BloomFilterHeader</c>). The only filter the format defines is split block, hashed with xxHash64 and not compressed.</summary>
public sealed class BloomFilterHeader : IThriftStruct<BloomFilterHeader>
{
    /// <summary>The size of the bits of the filter in bytes.</summary>
    public int NumBytes { get; set; }

    /// <summary>Whether the algorithm, the hash and the compression are the ones this library knows: split block, xxHash64, none.</summary>
    public bool IsSupported { get; set; } = true;

    public static BloomFilterHeader Deserialize(ReadOnlySpan<byte> data, out int bytesConsumed) =>
        ThriftStruct.Deserialize<BloomFilterHeader>(data, out bytesConsumed);

    public void Serialize(IBufferWriter<byte> output) => ThriftStruct.Serialize(this, output);

    static BloomFilterHeader IThriftStruct<BloomFilterHeader>.Read(ref ThriftCompactReader reader)
    {
        var header = new BloomFilterHeader { IsSupported = true };
        bool algorithm = false;
        bool hash = false;
        bool compression = false;
        reader.BeginStruct();
        while (reader.TryReadField(out short id, out var type))
        {
            switch (id)
            {
                case 1 when type == CompactType.I32: header.NumBytes = reader.ReadI32(); break;
                case 2 when type == CompactType.Struct: algorithm = ReadUnionIsFirstMember(ref reader); break;
                case 3 when type == CompactType.Struct: hash = ReadUnionIsFirstMember(ref reader); break;
                case 4 when type == CompactType.Struct: compression = ReadUnionIsFirstMember(ref reader); break;
                default: reader.Skip(type); break;
            }
        }

        reader.EndStruct();
        header.IsSupported = algorithm && hash && compression;
        return header;
    }

    void IThriftStruct<BloomFilterHeader>.Write(ref ThriftCompactWriter writer)
    {
        writer.BeginStruct();
        writer.WriteI32Field(1, NumBytes);
        WriteUnionOfFirstMember(ref writer, 2);
        WriteUnionOfFirstMember(ref writer, 3);
        WriteUnionOfFirstMember(ref writer, 4);
        writer.EndStruct();
    }

    /// <summary>Reads a union of which the first member is the one this library knows: true when that one is set.</summary>
    private static bool ReadUnionIsFirstMember(ref ThriftCompactReader reader)
    {
        bool first = false;
        reader.BeginStruct();
        while (reader.TryReadField(out short id, out var type))
        {
            first |= id == 1 && type == CompactType.Struct;
            reader.Skip(type);
        }

        reader.EndStruct();
        return first;
    }

    /// <summary>Writes a union whose first member is set to an empty struct: <c>BLOCK</c>, <c>XXHASH</c> and <c>UNCOMPRESSED</c> are that.</summary>
    private static void WriteUnionOfFirstMember(ref ThriftCompactWriter writer, short id)
    {
        writer.BeginStructField(id);
        writer.BeginStructField(1);
        writer.EndStruct();
        writer.EndStruct();
    }
}
