using System.Buffers.Binary;

namespace ParquetNeo.Geospatial;

/// <summary>
/// Walks the bytes of one geometry in the Well-Known Binary format of ISO 13249 (points, lines, polygons, their multi forms and collections, with
/// an optional Z and M) and tells a sink what is in them, without making objects of it. A value that is not valid, or has bytes left over, is refused.
/// </summary>
internal ref struct WkbScanner(ReadOnlySpan<byte> data)
{
    private const int MaxDepth = 32;

    private readonly ReadOnlySpan<byte> _data = data;
    private int _position;

    /// <summary>Scans the whole value; false when it is not a valid geometry. The sink may have been told of part of it by then.</summary>
    public bool TryScan(IWkbSink sink) => TryGeometry(sink, topLevel: true, depth: 0) && _position == _data.Length;

    private bool TryGeometry(IWkbSink sink, bool topLevel, int depth)
    {
        if (depth > MaxDepth || !TryReadByteOrder(out bool little) || !TryReadUInt32(little, out uint code))
        {
            return false;
        }

        int baseType = (int)(code % 1000);
        int dimensions = (int)(code / 1000);
        if (code >= 4000 || baseType is < 1 or > 7)
        {
            return false;
        }

        sink.Geometry((int)code, topLevel);
        bool hasZ = dimensions is 1 or 3;
        bool hasM = dimensions is 2 or 3;
        return baseType switch
        {
            1 => TryPath(sink, little, hasZ, hasM, isRing: false, count: 1),
            2 => TryCountedPath(sink, little, hasZ, hasM, isRing: false),
            3 => TryPolygon(sink, little, hasZ, hasM),
            _ => TryCollection(sink, little, depth),
        };
    }

    private bool TryPolygon(IWkbSink sink, bool little, bool hasZ, bool hasM)
    {
        if (!TryReadCount(little, minimumSize: sizeof(uint), out int rings))
        {
            return false;
        }

        for (int ring = 0; ring < rings; ring++)
        {
            if (!TryCountedPath(sink, little, hasZ, hasM, isRing: true))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryCollection(IWkbSink sink, bool little, int depth)
    {
        // A geometry takes 5 bytes at least: its byte order and its type.
        if (!TryReadCount(little, minimumSize: 5, out int count))
        {
            return false;
        }

        for (int i = 0; i < count; i++)
        {
            if (!TryGeometry(sink, topLevel: false, depth + 1))
            {
                return false;
            }
        }

        return true;
    }

    private bool TryCountedPath(IWkbSink sink, bool little, bool hasZ, bool hasM, bool isRing)
    {
        int size = CoordinateSize(hasZ, hasM);
        return TryReadCount(little, size, out int count) && TryPath(sink, little, hasZ, hasM, isRing, count);
    }

    private bool TryPath(IWkbSink sink, bool little, bool hasZ, bool hasM, bool isRing, int count)
    {
        int size = CoordinateSize(hasZ, hasM);
        if ((long)count * size > _data.Length - _position)
        {
            return false;
        }

        sink.BeginPath(isRing);
        for (int i = 0; i < count; i++)
        {
            double x = ReadDouble(little);
            double y = ReadDouble(little);
            double z = hasZ ? ReadDouble(little) : double.NaN;
            double m = hasM ? ReadDouble(little) : double.NaN;
            sink.Vertex(x, y, z, m);
        }

        sink.EndPath();
        return true;
    }

    private static int CoordinateSize(bool hasZ, bool hasM) => (2 + (hasZ ? 1 : 0) + (hasM ? 1 : 0)) * sizeof(double);

    private bool TryReadByteOrder(out bool little)
    {
        little = false;
        if (_position >= _data.Length)
        {
            return false;
        }

        byte order = _data[_position++];
        little = order == 1;
        return order <= 1;
    }

    private bool TryReadUInt32(bool little, out uint value)
    {
        value = 0;
        if (_data.Length - _position < sizeof(uint))
        {
            return false;
        }

        var bytes = _data.Slice(_position, sizeof(uint));
        value = little ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : BinaryPrimitives.ReadUInt32BigEndian(bytes);
        _position += sizeof(uint);
        return true;
    }

    /// <summary>Reads a number of items and checks that the bytes that are left could hold that many of at least <paramref name="minimumSize"/> bytes, so that a count cannot be made up to loop for long.</summary>
    private bool TryReadCount(bool little, int minimumSize, out int count)
    {
        count = 0;
        if (!TryReadUInt32(little, out uint value) || value > int.MaxValue || (long)value * minimumSize > _data.Length - _position)
        {
            return false;
        }

        count = (int)value;
        return true;
    }

    private double ReadDouble(bool little)
    {
        var bytes = _data.Slice(_position, sizeof(double));
        _position += sizeof(double);
        return little ? BinaryPrimitives.ReadDoubleLittleEndian(bytes) : BinaryPrimitives.ReadDoubleBigEndian(bytes);
    }
}
