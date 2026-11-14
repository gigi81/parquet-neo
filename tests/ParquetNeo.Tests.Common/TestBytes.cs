using System.Buffers;
using System.Buffers.Binary;
using ParquetNeo.Metadata;
using ParquetNeo.Thrift;

namespace ParquetNeo.Tests;

internal delegate void WriterAction(ref ThriftCompactWriter writer);

internal delegate T ReaderFunc<out T>(ref ThriftCompactReader reader);

internal static class TestBytes
{
    /// <summary>Parses "15 02 00" style hex.</summary>
    public static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", string.Empty));

    public static string ToHex(ReadOnlySpan<byte> bytes) => Convert.ToHexString(bytes);

    /// <summary>Same normalization as <see cref="ToHex"/> for a hand-written "15 02 00" literal.</summary>
    public static string Normalize(string hex) => hex.Replace(" ", string.Empty).ToUpperInvariant();

    public static byte[] Write(WriterAction action)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var writer = new ThriftCompactWriter(buffer);
        action(ref writer);
        return buffer.WrittenSpan.ToArray();
    }

    public static T Read<T>(byte[] data, ReaderFunc<T> action)
    {
        var reader = new ThriftCompactReader(data);
        return action(ref reader);
    }

    public static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    public static FileMetaData ReadMetadata(string fixture) =>
        FileMetaData.Deserialize(ReadFooterBytes(File.ReadAllBytes(FixturePath(fixture))));

    /// <summary>Footer layout: ... footer | 4-byte little-endian footer length | "PAR1".</summary>
    public static byte[] ReadFooterBytes(byte[] file)
    {
        var tail = file.AsSpan(file.Length - 8);
        if (!tail[4..].SequenceEqual("PAR1"u8))
        {
            throw new InvalidDataException("Missing trailing PAR1 magic.");
        }

        int length = BinaryPrimitives.ReadInt32LittleEndian(tail);
        return file.AsSpan(file.Length - 8 - length, length).ToArray();
    }

    public static string ToHex(SchemaElement element)
    {
        var buffer = new ArrayBufferWriter<byte>();
        ThriftStruct.Serialize(element, buffer);
        return ToHex(buffer.WrittenSpan);
    }
}
