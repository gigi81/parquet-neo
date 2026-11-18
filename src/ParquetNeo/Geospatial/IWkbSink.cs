namespace ParquetNeo.Geospatial;

/// <summary>What <see cref="WkbScanner"/> finds in a geometry, in the order it is in the bytes.</summary>
internal interface IWkbSink
{
    /// <summary>A geometry starts, with its ISO type code (1 to 7, plus 1000 for a Z, 2000 for an M, 3000 for both); <paramref name="topLevel"/> is false for one inside a collection.</summary>
    void Geometry(int isoType, bool topLevel);

    /// <summary>A line, a ring of a polygon, or a point starts: the vertices that follow are joined by edges until <see cref="EndPath"/>.</summary>
    void BeginPath(bool isRing);

    /// <summary>A coordinate; <paramref name="z"/> and <paramref name="m"/> are NaN when the geometry has none.</summary>
    void Vertex(double x, double y, double z, double m);

    void EndPath();
}
