using ParquetNeo.DataModel;

namespace ParquetNeo.Serialization.Tests.Generated;

/// <summary>A row with geometries and geographies of every kind of annotation, with a generated mapping.</summary>
[ParquetSerializable]
public sealed class GeoPlace
{
    public int Id { get; set; }

    [ParquetGeometry]
    public byte[]? Shape { get; set; }

    [ParquetGeometry(Crs = "EPSG:3857")]
    public byte[] Projected { get; set; } = [];

    [ParquetGeography]
    public byte[]? Region { get; set; }

    [ParquetGeography(Crs = "EPSG:4326", Algorithm = EdgeInterpolationAlgorithm.Vincenty)]
    public byte[]? Exact { get; set; }

    public List<byte[]>? Plain { get; set; }
}
