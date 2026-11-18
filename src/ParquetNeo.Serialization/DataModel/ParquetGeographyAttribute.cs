
namespace ParquetNeo.DataModel;

/// <summary>
/// Makes the column of a <see cref="byte"/> array property a GEOGRAPHY: the bytes of each value are a feature in the Well-Known Binary format
/// with longitudes from -180 to 180 and latitudes from -90 to 90, whose edges follow the <see cref="Algorithm"/>, great circle arcs on a sphere
/// unless it says otherwise. The footer records the bounding box (which can cross the antimeridian) and the types of the features of every row
/// group for spherical edges, which <c>ParquetFilter.Column(...).IntersectsBox(...)</c> uses to skip row groups.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class ParquetGeographyAttribute : Attribute
{
    /// <summary>The coordinate reference system; null for the default, <c>OGC:CRS84</c>.</summary>
    public string? Crs { get; set; }

    /// <summary>How the edges between vertices are drawn; <see cref="EdgeInterpolationAlgorithm.Spherical"/> when not set.</summary>
    public EdgeInterpolationAlgorithm Algorithm { get; set; } = EdgeInterpolationAlgorithm.Spherical;
}
