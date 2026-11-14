using System.Buffers.Binary;

namespace ParquetNeo.Filtering;

/// <summary>The plain encoding of a value of a filter in a column, see <see cref="PlainValueKind"/>.</summary>
internal readonly record struct PlainValue(PlainValueKind Kind, byte[]? Bytes)
{
    public static PlainValue Unsupported => new(PlainValueKind.Unsupported, null);

    public static PlainValue NotStorable => new(PlainValueKind.NotStorable, null);

    public static PlainValue Of(byte[] bytes) => new(PlainValueKind.Bytes, bytes);

    public static PlainValue Of(int value)
    {
        var bytes = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        return Of(bytes);
    }

    public static PlainValue Of(long value)
    {
        var bytes = new byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
        return Of(bytes);
    }
}
