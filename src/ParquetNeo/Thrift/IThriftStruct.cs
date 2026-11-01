using System.Runtime.CompilerServices;
using System.Buffers;

namespace ParquetNeo.Thrift;

/// <summary>A Thrift struct that can read and write itself with the compact protocol.</summary>
internal interface IThriftStruct<TSelf> where TSelf : IThriftStruct<TSelf>
{
    static abstract TSelf Read(ref ThriftCompactReader reader);

    void Write(ref ThriftCompactWriter writer);
}

/// <summary>Entry points and list helpers shared by all metadata structs.</summary>
internal static class ThriftStruct
{
    public static T Deserialize<T>(ReadOnlySpan<byte> data, out int bytesConsumed) where T : IThriftStruct<T>
    {
        var reader = new ThriftCompactReader(data);
        var value = T.Read(ref reader);
        bytesConsumed = reader.Position;
        return value;
    }

    public static void Serialize<T>(T value, IBufferWriter<byte> output) where T : IThriftStruct<T>
    {
        var writer = new ThriftCompactWriter(output);
        value.Write(ref writer);
    }

    public static T ReadStruct<T>(ref ThriftCompactReader reader) where T : IThriftStruct<T> => T.Read(ref reader);

    public static void WriteStructField<T>(ref ThriftCompactWriter writer, short id, T value) where T : IThriftStruct<T>
    {
        writer.WriteStructFieldHeader(id);
        value.Write(ref writer);
    }

    public static List<T> ReadStructList<T>(ref ThriftCompactReader reader) where T : IThriftStruct<T>
    {
        int count = reader.ReadListHeader(CompactType.Struct);
        var list = new List<T>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(T.Read(ref reader));
        }

        return list;
    }

    public static List<string> ReadStringList(ref ThriftCompactReader reader)
    {
        int count = reader.ReadListHeader(CompactType.Binary);
        var list = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(reader.ReadString());
        }

        return list;
    }

    public static List<long> ReadI64List(ref ThriftCompactReader reader)
    {
        int count = reader.ReadListHeader(CompactType.I64);
        var list = new List<long>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(reader.ReadI64());
        }

        return list;
    }

    /// <summary>Reads a list of i32-backed enum values; unknown values are preserved as-is.</summary>
    public static List<TEnum> ReadEnumList<TEnum>(ref ThriftCompactReader reader) where TEnum : unmanaged, Enum
    {
        int count = reader.ReadListHeader(CompactType.I32);
        var list = new List<TEnum>(count);
        for (int i = 0; i < count; i++)
        {
            list.Add(Unsafe.BitCast<int, TEnum>(reader.ReadI32()));
        }

        return list;
    }

    public static void WriteStructList<T>(ref ThriftCompactWriter writer, short id, List<T> items)
        where T : IThriftStruct<T>
    {
        writer.BeginListField(id, CompactType.Struct, items.Count);
        foreach (var item in items)
        {
            item.Write(ref writer);
        }
    }

    public static void WriteStringList(ref ThriftCompactWriter writer, short id, List<string> items)
    {
        writer.BeginListField(id, CompactType.Binary, items.Count);
        foreach (var item in items)
        {
            writer.WriteString(item);
        }
    }

    public static void WriteI64List(ref ThriftCompactWriter writer, short id, List<long> items)
    {
        writer.BeginListField(id, CompactType.I64, items.Count);
        foreach (var item in items)
        {
            writer.WriteI64(item);
        }
    }

    public static void WriteEnumList<TEnum>(ref ThriftCompactWriter writer, short id, List<TEnum> items)
        where TEnum : unmanaged, Enum
    {
        writer.BeginListField(id, CompactType.I32, items.Count);
        foreach (var item in items)
        {
            writer.WriteI32(Unsafe.BitCast<TEnum, int>(item));
        }
    }
}
