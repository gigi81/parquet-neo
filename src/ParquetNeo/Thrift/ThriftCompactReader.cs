using System.Buffers.Binary;
using System.Text;

namespace ParquetNeo.Thrift;

/// <summary>
/// Forward-only reader for the Thrift compact protocol over a contiguous buffer.
/// Every length and count read from the data is validated against the remaining bytes, and nesting
/// is bounded, so malformed input throws <see cref="ParquetFormatException"/> instead of
/// over-allocating or overflowing the stack.
/// </summary>
internal ref struct ThriftCompactReader
{
    private readonly ReadOnlySpan<byte> _data;
    private int _position;
    private int _depth;
    private short _lastFieldId;
    private FieldIdStack _fieldIds;

    public ThriftCompactReader(ReadOnlySpan<byte> data)
    {
        _data = data;
    }

    public readonly int Position => _position;

    public readonly int Remaining => _data.Length - _position;

    /// <summary>Enters a struct: field ids restart from zero until <see cref="EndStruct"/>.</summary>
    public void BeginStruct()
    {
        Enter();
        _fieldIds[_depth - 1] = _lastFieldId;
        _lastFieldId = 0;
    }

    public void EndStruct()
    {
        _lastFieldId = _fieldIds[--_depth];
    }

    /// <summary>Reads the next field header; returns false when the struct's stop byte is reached.</summary>
    public bool TryReadField(out short fieldId, out CompactType type)
    {
        byte header = ReadByte();
        if (header == 0)
        {
            fieldId = 0;
            type = CompactType.Stop;
            return false;
        }

        type = (CompactType)(header & 0x0F);
        if (type is CompactType.Stop or > CompactType.Struct)
        {
            throw Error($"invalid field type {header & 0x0F}");
        }

        int delta = header >> 4;
        fieldId = delta == 0 ? ReadI16() : (short)(_lastFieldId + delta);
        _lastFieldId = fieldId;
        return true;
    }

    public sbyte ReadI8() => (sbyte)ReadByte();

    public short ReadI16() => (short)ReadI32();

    public int ReadI32()
    {
        uint value = ReadVarint32();
        return (int)(value >> 1) ^ -(int)(value & 1);
    }

    public long ReadI64()
    {
        ulong value = ReadVarint64();
        return (long)(value >> 1) ^ -(long)(value & 1);
    }

    public double ReadDouble()
    {
        var bytes = Take(sizeof(double));
        return BinaryPrimitives.ReadDoubleLittleEndian(bytes);
    }

    /// <summary>Reads a bool list/set element (1 = true; the field-header form carries the value in the type).</summary>
    public bool ReadBoolElement() => ReadByte() == (byte)CompactType.BoolTrue;

    public ReadOnlySpan<byte> ReadBinary() => Take(ReadLength());

    public byte[] ReadBytes() => ReadBinary().ToArray();

    public string ReadString() => Encoding.UTF8.GetString(ReadBinary());

    /// <summary>Reads a list/set header and checks the element type; returns the element count.</summary>
    public int ReadListHeader(CompactType expectedElementType)
    {
        int count = ReadListHeader(out var elementType);
        bool bothBool = IsBool(expectedElementType) && IsBool(elementType);
        if (elementType != expectedElementType && !bothBool)
        {
            throw Error($"expected list of {expectedElementType} but found {elementType}");
        }

        return count;
    }

    public int ReadListHeader(out CompactType elementType)
    {
        byte header = ReadByte();
        elementType = (CompactType)(header & 0x0F);
        if (elementType is CompactType.Stop or > CompactType.Struct)
        {
            throw Error($"invalid list element type {header & 0x0F}");
        }

        int count = header >> 4;
        if (count == 15)
        {
            uint large = ReadVarint32();
            if (large > int.MaxValue)
            {
                throw Error("list too large");
            }

            count = (int)large;
        }

        // Every element occupies at least one byte, so this bounds allocations by the input size.
        if (count > Remaining)
        {
            throw Error($"list of {count} elements exceeds remaining {Remaining} bytes");
        }

        return count;
    }

    /// <summary>Skips a value of the given type (as found in a field header).</summary>
    public void Skip(CompactType type)
    {
        switch (type)
        {
            case CompactType.BoolTrue:
            case CompactType.BoolFalse:
                break; // the value is part of the field header
            case CompactType.I8:
                Take(1);
                break;
            case CompactType.I16:
            case CompactType.I32:
                ReadVarint32();
                break;
            case CompactType.I64:
                ReadVarint64();
                break;
            case CompactType.Double:
                Take(sizeof(double));
                break;
            case CompactType.Binary:
                Take(ReadLength());
                break;
            case CompactType.List:
            case CompactType.Set:
                SkipList();
                break;
            case CompactType.Map:
                SkipMap();
                break;
            case CompactType.Struct:
                SkipStruct();
                break;
            default:
                throw Error($"cannot skip type {type}");
        }
    }

    private void SkipStruct()
    {
        BeginStruct();
        while (TryReadField(out _, out var type))
        {
            Skip(type);
        }

        EndStruct();
    }

    private void SkipList()
    {
        int count = ReadListHeader(out var elementType);
        Enter();
        for (int i = 0; i < count; i++)
        {
            SkipElement(elementType);
        }

        _depth--;
    }

    private void SkipMap()
    {
        uint size = ReadVarint32();
        if (size == 0)
        {
            return;
        }

        byte types = ReadByte();
        var keyType = (CompactType)(types >> 4);
        var valueType = (CompactType)(types & 0x0F);
        if (size > (uint)Remaining) // each entry takes at least one byte
        {
            throw Error($"map of {size} entries exceeds remaining {Remaining} bytes");
        }

        Enter();
        for (uint i = 0; i < size; i++)
        {
            SkipElement(keyType);
            SkipElement(valueType);
        }

        _depth--;
    }

    /// <summary>Collection elements of bool type occupy a byte, unlike bool struct fields.</summary>
    private void SkipElement(CompactType type)
    {
        if (IsBool(type))
        {
            Take(1);
        }
        else
        {
            Skip(type);
        }
    }

    private static bool IsBool(CompactType type) => type is CompactType.BoolTrue or CompactType.BoolFalse;

    private void Enter()
    {
        if (_depth >= FieldIdStack.Size)
        {
            throw Error("nesting too deep");
        }

        _depth++;
    }

    private int ReadLength()
    {
        uint length = ReadVarint32();
        if (length > (uint)Remaining)
        {
            throw Error($"length {length} exceeds remaining {Remaining} bytes");
        }

        return (int)length;
    }

    private byte ReadByte()
    {
        if (_position >= _data.Length)
        {
            throw Error("unexpected end of data");
        }

        return _data[_position++];
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if ((uint)count > (uint)Remaining)
        {
            throw Error("unexpected end of data");
        }

        var slice = _data.Slice(_position, count);
        _position += count;
        return slice;
    }

    private uint ReadVarint32()
    {
        uint result = 0;
        for (int shift = 0; shift < 35; shift += 7)
        {
            byte b = ReadByte();
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }
        }

        throw Error("varint too long");
    }

    private ulong ReadVarint64()
    {
        ulong result = 0;
        for (int shift = 0; shift < 70; shift += 7)
        {
            byte b = ReadByte();
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                return result;
            }
        }

        throw Error("varint too long");
    }

    private readonly ParquetFormatException Error(string message) =>
        new($"Invalid Thrift data at offset {_position}: {message}.");
}
