using System.Buffers.Binary;
using System.Buffers;
using System.Text;

namespace ParquetNeo.Thrift;

/// <summary>Writer for the Thrift compact protocol into an <see cref="IBufferWriter{T}"/>.</summary>
internal ref struct ThriftCompactWriter
{
    private readonly IBufferWriter<byte> _output;
    private int _depth;
    private short _lastFieldId;
    public FieldIdStack FieldIds;

    public ThriftCompactWriter(IBufferWriter<byte> output)
    {
        _output = output;
    }

    public void BeginStruct()
    {
        if (_depth >= FieldIdStack.Size)
        {
            throw new InvalidOperationException("Thrift structure nesting too deep.");
        }

        FieldIds[_depth++] = _lastFieldId;
        _lastFieldId = 0;
    }

    public void EndStruct()
    {
        WriteByte(0);
        _lastFieldId = FieldIds[--_depth];
    }

    public void WriteBoolField(short id, bool value) =>
        WriteFieldHeader(id, value ? CompactType.BoolTrue : CompactType.BoolFalse);

    public void WriteI8Field(short id, sbyte value)
    {
        WriteFieldHeader(id, CompactType.I8);
        WriteByte((byte)value);
    }

    public void WriteI16Field(short id, short value)
    {
        WriteFieldHeader(id, CompactType.I16);
        WriteI32(value);
    }

    public void WriteI32Field(short id, int value)
    {
        WriteFieldHeader(id, CompactType.I32);
        WriteI32(value);
    }

    public void WriteI64Field(short id, long value)
    {
        WriteFieldHeader(id, CompactType.I64);
        WriteI64(value);
    }

    public void WriteDoubleField(short id, double value)
    {
        WriteFieldHeader(id, CompactType.Double);
        WriteDouble(value);
    }

    public void WriteBinaryField(short id, ReadOnlySpan<byte> value)
    {
        WriteFieldHeader(id, CompactType.Binary);
        WriteBinary(value);
    }

    public void WriteStringField(short id, string value)
    {
        WriteFieldHeader(id, CompactType.Binary);
        WriteString(value);
    }

    /// <summary>Writes only the header of a struct-typed field; the struct writes its own framing.</summary>
    public void WriteStructFieldHeader(short id) => WriteFieldHeader(id, CompactType.Struct);

    /// <summary>Writes the header of a struct-typed field and enters the struct.</summary>
    public void BeginStructField(short id)
    {
        WriteFieldHeader(id, CompactType.Struct);
        BeginStruct();
    }

    /// <summary>Writes the header of a list-typed field; the caller then writes <paramref name="count"/> elements.</summary>
    public void BeginListField(short id, CompactType elementType, int count)
    {
        WriteFieldHeader(id, CompactType.List);
        WriteListHeader(elementType, count);
    }

    public void WriteListHeader(CompactType elementType, int count)
    {
        if (count < 15)
        {
            WriteByte((byte)((count << 4) | (byte)elementType));
        }
        else
        {
            WriteByte((byte)(0xF0 | (byte)elementType));
            WriteVarint((uint)count);
        }
    }

    public void WriteBoolElement(bool value) =>
        WriteByte(value ? (byte)CompactType.BoolTrue : (byte)CompactType.BoolFalse);

    public void WriteI32(int value) => WriteVarint((uint)((value << 1) ^ (value >> 31)));

    public void WriteI64(long value) => WriteVarint((ulong)((value << 1) ^ (value >> 63)));

    public void WriteDouble(double value)
    {
        var span = _output.GetSpan(sizeof(double));
        BinaryPrimitives.WriteDoubleLittleEndian(span, value);
        _output.Advance(sizeof(double));
    }

    public void WriteBinary(ReadOnlySpan<byte> value)
    {
        WriteVarint((uint)value.Length);
        var span = _output.GetSpan(value.Length);
        value.CopyTo(span);
        _output.Advance(value.Length);
    }

    public void WriteString(string value)
    {
        int length = Encoding.UTF8.GetByteCount(value);
        WriteVarint((uint)length);
        var span = _output.GetSpan(length);
        Encoding.UTF8.GetBytes(value, span);
        _output.Advance(length);
    }

    private void WriteFieldHeader(short id, CompactType type)
    {
        int delta = id - _lastFieldId;
        if (delta is > 0 and <= 15)
        {
            WriteByte((byte)((delta << 4) | (byte)type));
        }
        else
        {
            WriteByte((byte)type);
            WriteI32(id);
        }

        _lastFieldId = id;
    }

    private void WriteByte(byte value)
    {
        var span = _output.GetSpan(1);
        span[0] = value;
        _output.Advance(1);
    }

    private void WriteVarint(ulong value)
    {
        var span = _output.GetSpan(10);
        int length = 0;
        while (value >= 0x80)
        {
            span[length++] = (byte)(value | 0x80);
            value >>= 7;
        }

        span[length++] = (byte)value;
        _output.Advance(length);
    }
}
