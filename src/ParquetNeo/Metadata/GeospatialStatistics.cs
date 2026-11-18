using ParquetNeo.Thrift;

namespace ParquetNeo.Metadata;

/// <summary>What is known of the features of a GEOMETRY or GEOGRAPHY column chunk (thrift <c>GeospatialStatistics</c>).</summary>
public sealed class GeospatialStatistics : IThriftStruct<GeospatialStatistics>
{
    /// <summary>The box that holds all the features, or null when the writer did not record it or it cannot be told.</summary>
    public BoundingBox? BoundingBox { get; set; }

    /// <summary>
    /// The geometry types of the features as the codes of ISO Well-Known Binary: 1 for a point up to 7 for a collection, with 1000 added for
    /// coordinates with a Z, 2000 with an M and 3000 with both (1001 is a point with a Z). Null, or empty, when the writer did not record them.
    /// </summary>
    public List<int>? GeospatialTypes { get; set; }

    static GeospatialStatistics IThriftStruct<GeospatialStatistics>.Read(ref ThriftCompactReader reader)
    {
        var statistics = new GeospatialStatistics();
        reader.BeginStruct();
        while (reader.TryReadField(out short id, out var type))
        {
            switch (id)
            {
                case 1 when type == CompactType.Struct: statistics.BoundingBox = ThriftStruct.ReadStruct<BoundingBox>(ref reader); break;
                case 2 when type == CompactType.List: statistics.GeospatialTypes = ThriftStruct.ReadI32List(ref reader); break;
                default: reader.Skip(type); break;
            }
        }

        reader.EndStruct();
        return statistics;
    }

    void IThriftStruct<GeospatialStatistics>.Write(ref ThriftCompactWriter writer)
    {
        writer.BeginStruct();
        if (BoundingBox is not null) ThriftStruct.WriteStructField(ref writer, 1, BoundingBox);
        if (GeospatialTypes is not null) ThriftStruct.WriteI32List(ref writer, 2, GeospatialTypes);
        writer.EndStruct();
    }
}
