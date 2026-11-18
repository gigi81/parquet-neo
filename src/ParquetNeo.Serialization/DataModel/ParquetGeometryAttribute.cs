
namespace ParquetNeo.DataModel;

/// <summary>
/// Makes the column of a <see cref="byte"/> array property a GEOMETRY: the bytes of each value are a feature in the Well-Known Binary format
/// (ISO 13249, as PostGIS, NetTopologySuite, GEOS and the others write it), with straight edges. The footer then records the bounding box and
/// the types of the features of every row group, which <c>ParquetFilter.Column(...).IntersectsBox(...)</c> uses to skip row groups.
/// The bytes are stored as they are given: they are not checked, and a value that is not valid Well-Known Binary leaves its row group without a box.
/// </summary>
[AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
public sealed class ParquetGeometryAttribute : Attribute
{
    /// <summary>The coordinate reference system, such as <c>EPSG:3857</c>; null for the default, <c>OGC:CRS84</c> (longitude and latitude, WGS84).</summary>
    public string? Crs { get; set; }
}
