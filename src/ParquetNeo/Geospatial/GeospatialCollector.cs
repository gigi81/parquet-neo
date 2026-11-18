using ParquetNeo.Metadata;

namespace ParquetNeo.Geospatial;

/// <summary>
/// Collects the bounding box and the geometry types of the values of a GEOMETRY or GEOGRAPHY column chunk, from their bytes, for the footer.
/// The box of a geometry holds its coordinates. The box of a geography on a sphere also holds its edges, which are great circle arcs that can
/// go further north or south than their ends, and can cross the antimeridian (then the west end is greater than the east end); a polygon that
/// goes around a pole reaches it. For another way of drawing edges no box is recorded: a box that is too small would skip features that match.
/// A value that is not valid Well-Known Binary leaves the chunk without statistics.
/// </summary>
internal sealed class GeospatialCollector : IWkbSink
{
    private const double Epsilon = 1e-12;

    private readonly bool _geography;
    private readonly bool _boxIsKnowable;
    private readonly SortedSet<int> _types = [];
    private readonly LongitudeCover _cover = new();
    private double _xMin = double.PositiveInfinity, _xMax = double.NegativeInfinity, _yMin = double.PositiveInfinity, _yMax = double.NegativeInfinity;
    private double _zMin = double.PositiveInfinity, _zMax = double.NegativeInfinity, _mMin = double.PositiveInfinity, _mMax = double.NegativeInfinity;
    private bool _invalid;
    private bool _boxUnknown;
    private bool _allLongitudes;
    private bool _anyVertex;

    // The path being read, for a geography.
    private bool _inRing;
    private bool _hasPrevious;
    private double _previousLongitude, _previousLatitude;
    private double _winding, _latitudeSum;
    private int _pathVertices;

    public GeospatialCollector(LogicalType logicalType)
    {
        _geography = logicalType.Kind == LogicalTypeKind.Geography;
        _boxIsKnowable = !_geography || logicalType.Algorithm is null or EdgeInterpolationAlgorithm.Spherical;
        _boxUnknown = !_boxIsKnowable;
    }

    /// <summary>Takes the bytes of one value.</summary>
    public void Add(ReadOnlySpan<byte> wkb)
    {
        if (_invalid)
        {
            return;
        }

        if (!new WkbScanner(wkb).TryScan(this))
        {
            _invalid = true;
        }
    }

    /// <summary>What was collected, or null when a value was not valid or nothing is known.</summary>
    public GeospatialStatistics? Build()
    {
        if (_invalid)
        {
            return null;
        }

        var box = BuildBox();
        if (box is null && _types.Count == 0)
        {
            return null;
        }

        return new GeospatialStatistics { BoundingBox = box, GeospatialTypes = [.. _types] };
    }

    void IWkbSink.Geometry(int isoType, bool topLevel)
    {
        if (topLevel)
        {
            _types.Add(isoType);
        }
    }

    void IWkbSink.BeginPath(bool isRing)
    {
        _inRing = isRing;
        _hasPrevious = false;
        _winding = 0;
        _latitudeSum = 0;
        _pathVertices = 0;
    }

    void IWkbSink.Vertex(double x, double y, double z, double m)
    {
        if (double.IsNaN(x) || double.IsNaN(y))
        {
            return; // an empty point
        }

        _anyVertex = true;
        Extend(ref _zMin, ref _zMax, z);
        Extend(ref _mMin, ref _mMax, m);
        if (!_geography)
        {
            Extend(ref _xMin, ref _xMax, x);
            Extend(ref _yMin, ref _yMax, y);
            return;
        }

        if (!_boxIsKnowable)
        {
            return;
        }

        if (x is < -180 or > 180 || y is < -90 or > 90)
        {
            _boxUnknown = true;
            return;
        }

        VertexOfGeography(x, y);
    }

    void IWkbSink.EndPath()
    {
        // A ring that goes once around the globe encloses a pole, which the box must hold though no edge reaches it.
        if (_geography && _inRing && Math.Abs(_winding) > 180 && _pathVertices > 0)
        {
            _allLongitudes = true;
            Extend(ref _yMin, ref _yMax, _latitudeSum >= 0 ? 90 : -90);
        }

        _inRing = false;
    }

    private void VertexOfGeography(double longitude, double latitude)
    {
        _cover.Add(Normalize(longitude), 0);
        Extend(ref _yMin, ref _yMax, latitude);
        _pathVertices++;
        _latitudeSum += latitude;
        if (_hasPrevious)
        {
            Edge(_previousLongitude, _previousLatitude, longitude, latitude);
        }

        _previousLongitude = longitude;
        _previousLatitude = latitude;
        _hasPrevious = true;
    }

    /// <summary>Adds what the great circle arc between two vertices covers beyond the vertices themselves.</summary>
    private void Edge(double longitude1, double latitude1, double longitude2, double latitude2)
    {
        double delta = longitude2 - longitude1;
        delta -= 360 * Math.Round(delta / 360);
        _winding += delta;
        if (Math.Abs(Math.Abs(delta) - 180) < 1e-9)
        {
            // Over a pole (or along a meridian that does): which way is not told by the ends, so hold both poles and every longitude.
            _allLongitudes = true;
            Extend(ref _yMin, ref _yMax, 90);
            Extend(ref _yMin, ref _yMax, -90);
            return;
        }

        if (delta >= 0)
        {
            _cover.Add(Normalize(longitude1), delta);
        }
        else
        {
            _cover.Add(Normalize(longitude2), -delta);
        }

        var first = UnitVector(longitude1, latitude1);
        var second = UnitVector(longitude2, latitude2);
        var normal = Cross(first, second);
        double length = Math.Sqrt(Dot(normal, normal));
        if (length < Epsilon)
        {
            return; // the same point twice
        }

        // The circle goes as far north as the angle its plane makes with the equator, at the point of the plane that is nearest to the north pole.
        var north = new Vector(-normal.Z * normal.X, -normal.Z * normal.Y, (length * length) - (normal.Z * normal.Z));
        double norm = Math.Sqrt(Dot(north, north));
        if (norm < Epsilon)
        {
            return; // the equator
        }

        north = new Vector(north.X / norm, north.Y / norm, north.Z / norm);
        if (OnArc(first, second, normal, north))
        {
            Extend(ref _yMin, ref _yMax, ToDegrees(Math.Asin(Math.Clamp(north.Z, -1, 1))));
        }

        var south = new Vector(-north.X, -north.Y, -north.Z);
        if (OnArc(first, second, normal, south))
        {
            Extend(ref _yMin, ref _yMax, ToDegrees(Math.Asin(Math.Clamp(south.Z, -1, 1))));
        }
    }

    private static bool OnArc(Vector first, Vector second, Vector normal, Vector point) =>
        Dot(Cross(first, point), normal) >= -Epsilon && Dot(Cross(point, second), normal) >= -Epsilon;

    private BoundingBox? BuildBox()
    {
        if (!_anyVertex || _boxUnknown)
        {
            return null;
        }

        var box = new BoundingBox { YMin = _yMin, YMax = _yMax };
        if (_geography)
        {
            (box.XMin, box.XMax) = _allLongitudes ? (-180, 180) : _cover.Cover();
        }
        else
        {
            box.XMin = _xMin;
            box.XMax = _xMax;
        }

        if (_zMin <= _zMax)
        {
            (box.ZMin, box.ZMax) = (_zMin, _zMax);
        }

        if (_mMin <= _mMax)
        {
            (box.MMin, box.MMax) = (_mMin, _mMax);
        }

        return box;
    }

    private static void Extend(ref double min, ref double max, double value)
    {
        if (double.IsNaN(value))
        {
            return;
        }

        min = Math.Min(min, value);
        max = Math.Max(max, value);
    }

    /// <summary>The longitude from -180 up to, not including, 180.</summary>
    private static double Normalize(double longitude) => longitude >= 180 ? -180 : longitude;

    private static Vector UnitVector(double longitude, double latitude)
    {
        double lon = ToRadians(longitude);
        double lat = ToRadians(latitude);
        return new Vector(Math.Cos(lat) * Math.Cos(lon), Math.Cos(lat) * Math.Sin(lon), Math.Sin(lat));
    }

    private static Vector Cross(Vector a, Vector b) =>
        new((a.Y * b.Z) - (a.Z * b.Y), (a.Z * b.X) - (a.X * b.Z), (a.X * b.Y) - (a.Y * b.X));

    private static double Dot(Vector a, Vector b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

    private static double ToRadians(double degrees) => degrees * Math.PI / 180;

    private static double ToDegrees(double radians) => radians * 180 / Math.PI;

    private readonly record struct Vector(double X, double Y, double Z);
}
