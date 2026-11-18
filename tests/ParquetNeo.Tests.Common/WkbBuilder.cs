using System.Buffers.Binary;

namespace ParquetNeo.Tests;

/// <summary>Builds the Well-Known Binary (ISO) of features, for the tests of geometry and geography columns.</summary>
public static class WkbBuilder
{
    /// <summary>A point; with <paramref name="z"/> it is a PointZ, with <paramref name="m"/> a PointM, with both a PointZM.</summary>
    public static byte[] Point(double x, double y, double? z = null, double? m = null, bool bigEndian = false)
    {
        var bytes = new List<byte>();
        Header(bytes, 1, z is not null, m is not null, bigEndian);
        Coordinate(bytes, x, y, z, m, bigEndian);
        return [.. bytes];
    }

    public static byte[] EmptyPoint(bool bigEndian = false) => Point(double.NaN, double.NaN, bigEndian: bigEndian);

    public static byte[] LineString((double X, double Y)[] points, bool bigEndian = false)
    {
        var bytes = new List<byte>();
        Header(bytes, 2, hasZ: false, hasM: false, bigEndian);
        Path(bytes, points, bigEndian);
        return [.. bytes];
    }

    /// <summary>A polygon of rings, each of points that start and end at the same one.</summary>
    public static byte[] Polygon(params (double X, double Y)[][] rings)
    {
        var bytes = new List<byte>();
        Header(bytes, 3, hasZ: false, hasM: false, bigEndian: false);
        Int(bytes, rings.Length, bigEndian: false);
        foreach (var ring in rings)
        {
            Path(bytes, ring, bigEndian: false);
        }

        return [.. bytes];
    }

    /// <summary>A multi point, multi line string, multi polygon or geometry collection (type 4 to 7) of whole features.</summary>
    public static byte[] Collection(int type, params byte[][] parts)
    {
        var bytes = new List<byte>();
        Header(bytes, type, hasZ: false, hasM: false, bigEndian: false);
        Int(bytes, parts.Length, bigEndian: false);
        foreach (var part in parts)
        {
            bytes.AddRange(part);
        }

        return [.. bytes];
    }

    /// <summary>A ring around the rectangle.</summary>
    public static (double X, double Y)[] Rectangle(double xMin, double yMin, double xMax, double yMax) =>
        [(xMin, yMin), (xMax, yMin), (xMax, yMax), (xMin, yMax), (xMin, yMin)];

    private static void Header(List<byte> bytes, int type, bool hasZ, bool hasM, bool bigEndian)
    {
        bytes.Add(bigEndian ? (byte)0 : (byte)1);
        Int(bytes, type + (hasZ ? 1000 : 0) + (hasM ? 2000 : 0), bigEndian);
    }

    private static void Path(List<byte> bytes, (double X, double Y)[] points, bool bigEndian)
    {
        Int(bytes, points.Length, bigEndian);
        foreach (var (x, y) in points)
        {
            Coordinate(bytes, x, y, null, null, bigEndian);
        }
    }

    private static void Coordinate(List<byte> bytes, double x, double y, double? z, double? m, bool bigEndian)
    {
        Double(bytes, x, bigEndian);
        Double(bytes, y, bigEndian);
        if (z is { } zValue)
        {
            Double(bytes, zValue, bigEndian);
        }

        if (m is { } mValue)
        {
            Double(bytes, mValue, bigEndian);
        }
    }

    private static void Int(List<byte> bytes, int value, bool bigEndian)
    {
        Span<byte> buffer = stackalloc byte[sizeof(int)];
        if (bigEndian)
        {
            BinaryPrimitives.WriteInt32BigEndian(buffer, value);
        }
        else
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        }

        bytes.AddRange(buffer.ToArray());
    }

    private static void Double(List<byte> bytes, double value, bool bigEndian)
    {
        Span<byte> buffer = stackalloc byte[sizeof(double)];
        if (bigEndian)
        {
            BinaryPrimitives.WriteDoubleBigEndian(buffer, value);
        }
        else
        {
            BinaryPrimitives.WriteDoubleLittleEndian(buffer, value);
        }

        bytes.AddRange(buffer.ToArray());
    }
}
