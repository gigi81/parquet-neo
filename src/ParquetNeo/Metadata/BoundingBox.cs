using ParquetNeo.Thrift;

namespace ParquetNeo.Metadata;

/// <summary>
/// The smallest and largest coordinate on each axis of a set of geospatial features (thrift <c>BoundingBox</c>). For a GEOGRAPHY the box can cross the
/// antimeridian, which is when <see cref="XMin"/> is greater than <see cref="XMax"/>: the box then holds the longitudes from <see cref="XMin"/> up to 180
/// and from -180 up to <see cref="XMax"/>.
/// </summary>
public sealed class BoundingBox : IThriftStruct<BoundingBox>
{
    public double XMin { get; set; }

    public double XMax { get; set; }

    public double YMin { get; set; }

    public double YMax { get; set; }

    /// <summary>The range of the third axis, when the features have one (XYZ or XYZM).</summary>
    public double? ZMin { get; set; }

    public double? ZMax { get; set; }

    /// <summary>The range of the measure, when the features have one (XYM or XYZM).</summary>
    public double? MMin { get; set; }

    public double? MMax { get; set; }

    /// <summary>Whether the box crosses the antimeridian: its longitudes wrap around from 180 to -180.</summary>
    public bool CrossesAntimeridian => XMin > XMax;

    /// <summary>Whether this box and the rectangle of <paramref name="xMin"/> to <paramref name="xMax"/> and <paramref name="yMin"/> to <paramref name="yMax"/> have a point in common; either can wrap around the antimeridian.</summary>
    public bool Intersects(double xMin, double yMin, double xMax, double yMax) =>
        YMin <= yMax && yMin <= YMax && XIntervalsMeet(XMin, XMax, xMin, xMax);

    private static bool XIntervalsMeet(double aMin, double aMax, double bMin, double bMax)
    {
        bool aWraps = aMin > aMax;
        bool bWraps = bMin > bMax;
        if (aWraps && bWraps)
        {
            return true; // both hold the antimeridian
        }

        if (aWraps)
        {
            return bMax >= aMin || bMin <= aMax;
        }

        if (bWraps)
        {
            return aMax >= bMin || aMin <= bMax;
        }

        return aMin <= bMax && bMin <= aMax;
    }

    static BoundingBox IThriftStruct<BoundingBox>.Read(ref ThriftCompactReader reader)
    {
        var box = new BoundingBox();
        reader.BeginStruct();
        while (reader.TryReadField(out short id, out var type))
        {
            if (type != CompactType.Double)
            {
                reader.Skip(type);
                continue;
            }

            switch (id)
            {
                case 1: box.XMin = reader.ReadDouble(); break;
                case 2: box.XMax = reader.ReadDouble(); break;
                case 3: box.YMin = reader.ReadDouble(); break;
                case 4: box.YMax = reader.ReadDouble(); break;
                case 5: box.ZMin = reader.ReadDouble(); break;
                case 6: box.ZMax = reader.ReadDouble(); break;
                case 7: box.MMin = reader.ReadDouble(); break;
                case 8: box.MMax = reader.ReadDouble(); break;
                default: reader.Skip(type); break;
            }
        }

        reader.EndStruct();
        return box;
    }

    void IThriftStruct<BoundingBox>.Write(ref ThriftCompactWriter writer)
    {
        writer.BeginStruct();
        writer.WriteDoubleField(1, XMin);
        writer.WriteDoubleField(2, XMax);
        writer.WriteDoubleField(3, YMin);
        writer.WriteDoubleField(4, YMax);
        if (ZMin is { } zMin) writer.WriteDoubleField(5, zMin);
        if (ZMax is { } zMax) writer.WriteDoubleField(6, zMax);
        if (MMin is { } mMin) writer.WriteDoubleField(7, mMin);
        if (MMax is { } mMax) writer.WriteDoubleField(8, mMax);
        writer.EndStruct();
    }
}
