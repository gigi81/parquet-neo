# Geospatial columns

Parquet has two annotations for geographic features: **GEOMETRY**, features on a plane with straight edges (projected coordinates, floor
plans, anything planar), and **GEOGRAPHY**, features on the globe whose edges follow an interpolation of the ellipsoid (longitude and
latitude). In both the value is a **BYTE_ARRAY** holding the feature in the *Well-Known Binary* (WKB) format that PostGIS, NetTopologySuite,
GEOS, GDAL and the others read and write. ParquetNeo does not have a geometry type of its own and does not look inside a feature to read or
write it: it stores the bytes you give it, annotated, and works out what the footer needs to skip row groups.

## Writing

On a class, mark the `byte[]` property:

```csharp
public class Place
{
    public int Id { get; set; }

    [ParquetGeometry(Crs = "EPSG:3857")]   // no Crs: OGC:CRS84, longitude and latitude on WGS84
    public byte[]? Footprint { get; set; }

    [ParquetGeography]                      // great circle edges on a sphere
    public byte[]? Region { get; set; }

    [ParquetGeography(Crs = "EPSG:4326", Algorithm = EdgeInterpolationAlgorithm.Karney)]
    public byte[]? Track { get; set; }
}
```

With the low-level API, annotate the column: `new PrimitiveNode("shape", PhysicalType.ByteArray, FieldRepetitionType.Optional, LogicalType.Geometry())`
(or `LogicalType.Geography(crs, algorithm)`), and write the bytes with `WriteByteArrays`.

For each row group the writer reads the bytes of the features and records, in the footer of the column chunk (`ColumnMetaData.GeospatialStatistics`):

- the **bounding box**: the smallest and largest X and Y, and Z and M when the coordinates have them;
- the **geometry types** present, as the ISO WKB codes: 1 point, 2 line string, 3 polygon, 4 to 7 the multi forms and collections, plus 1000 for a Z,
  2000 for an M and 3000 for both (1001 is a point with a Z). Only the type of a feature itself is listed, not those of the parts of a collection.

A geometry or geography has no sort order, so no minimum and maximum are written, only the null count (and the ones another writer may have
written are not believed). The statistics are recorded when `ParquetWriterOptions.StatisticsEnabled` is on, which it is by default.

A value that is not valid WKB is stored as it is, but its row group is left without geospatial statistics, since a box that leaves a feature out
would skip row groups that match.

### The box of a geography

The edges of a geography between two vertices are arcs of great circles on a sphere, which go further north or south than the vertices when
they are not on the equator: the arc from (0, 60) to (90, 60) reaches a latitude over 60. The writer takes that into account, and holds a pole
that a polygon goes around. It also knows that the shortest way between 170 and -170 degrees of longitude is across the antimeridian, so
the box of such features has its west end greater than its east end (`BoundingBox.CrossesAntimeridian`), as the specification defines.

For the other ways of drawing edges (`Vincenty`, `Thomas`, `Andoyer`, `Karney`) the box is not recorded, only the types: a box that is a little
too small would skip features that match, which is worse than none. A coordinate outside -180 to 180 or -90 to 90 leaves the box out too.

## Reading

The bytes come back as they were written: as a `byte[]` property of a class, as a `byte[]` from `ParquetDataReader`, and through
`ColumnReader.ReadByteArrays`. Hand them to your geometry library.

```csharp
var statistics = reader.OpenRowGroup(0).GetGeospatialStatistics("Footprint");
var box = statistics?.BoundingBox;          // XMin, XMax, YMin, YMax, ZMin, ...
var types = statistics?.GeospatialTypes;    // [1, 3]
```

## Skipping row groups

`IntersectsBox` is a condition for [filters](filtering.md): it rules out the row groups whose bounding box does not meet a rectangle.

```csharp
var filter = ParquetFilter.Column("Footprint").IntersectsBox(xMin: 12, yMin: 41, xMax: 13, yMax: 42);

var matching = reader.GetMatchingRowGroups(filter);
var places = ParquetSerializer.Deserialize<Place>(stream, new ParquetDeserializerOptions { RowGroupFilter = filter });
```

Like every filter it only prunes: the features of the row groups that are kept are not tested, so test them with your geometry library when the
answer must be exact. A row group with no recorded box is kept, as is a column that is not a geometry or geography, and a row group whose rows are
all null is skipped. For a geography, a rectangle with `xMin` greater than `xMax` crosses the antimeridian, as the boxes of the files do. It works
in `ParquetDataReaderOptions.RowGroupFilter` and the `RowGroupFilter` of the serializers as well. It cannot be written as a LINQ `Where`, since
there is no spatial type to test; pass the filter to `ParquetDeserializerOptions`.

Sorting the features by place before writing (a Hilbert curve or a grid cell, say) makes the boxes of the row groups small, which is what makes the
filter skip much of a file.

## What is not done

- Features are not parsed into objects, and no spatial predicate other than the bounding box is tested.
- The page index has no bounds for these columns, so there is no skipping of pages, only of row groups.
- `ParquetDataWriter` (ADO.NET) cannot tell that a `byte[]` column holds WKB, so it does not annotate it; write those with the serializer or the low-level API.
- Files with these columns have not been checked against other readers and writers (there are none in the test environment); the layout follows the specification.
