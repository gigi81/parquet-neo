using System.Buffers;
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
}
