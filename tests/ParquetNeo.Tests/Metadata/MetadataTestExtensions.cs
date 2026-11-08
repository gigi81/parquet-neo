using System.Buffers;
using ParquetNeo.Metadata;
using ParquetNeo.Metadata.Headers;

namespace ParquetNeo.Tests.Metadata;

internal static class MetadataTestExtensions
{
    public static byte[] ToBytes(this FileMetaData metadata)
    {
        var buffer = new ArrayBufferWriter<byte>();
        metadata.Serialize(buffer);
        return buffer.WrittenSpan.ToArray();
    }

    public static byte[] ToBytes(this PageHeader header)
    {
        var buffer = new ArrayBufferWriter<byte>();
        header.Serialize(buffer);
        return buffer.WrittenSpan.ToArray();
    }
}
