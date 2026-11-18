namespace ParquetNeo;

/// <summary>How the edges between the vertices of a GEOGRAPHY are drawn (thrift <c>EdgeInterpolationAlgorithm</c>); the values are those of the specification.</summary>
public enum EdgeInterpolationAlgorithm
{
    /// <summary>Great circle arcs on a sphere: the default.</summary>
    Spherical = 0,
    Vincenty = 1,
    Thomas = 2,
    Andoyer = 3,
    Karney = 4,
}
