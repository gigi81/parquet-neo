using ParquetNeo.Geospatial;
using ParquetNeo.Metadata;

namespace ParquetNeo.Statistics;

// A namespace called Statistics would otherwise hide the Statistics type of the metadata.
using Statistics = Metadata.Statistics;

/// <summary>
/// For a column of geometries or geographies, which have no sort order: the null count, and the bounding box and the types of the features,
/// worked out from the Well-Known Binary of the values.
/// </summary>
internal sealed class GeospatialStatisticsCollector(LogicalType logicalType) : StatisticsCollector
{
    private readonly GeospatialCollector _collector = new(logicalType);

    public override void AddBinary(ReadOnlySpan<byte> value) => _collector.Add(value);

    public override Statistics Build(long nullCount) => new() { NullCount = nullCount };

    public override GeospatialStatistics? BuildGeospatial() => _collector.Build();
}
