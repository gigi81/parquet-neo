
namespace ParquetNeo.ColumnTypes;

/// <summary>
/// What can be adjusted about the column a type is written to. A type takes the settings that suit it (see
/// <see cref="ColumnType{TValue}.SupportedSettings"/>) and ignores the others.
/// </summary>
public sealed record ColumnSettings
{
    /// <summary>The number of digits of a decimal.</summary>
    public int? Precision { get; init; }

    /// <summary>The number of digits of a decimal after the decimal point.</summary>
    public int? Scale { get; init; }

    /// <summary>The resolution of a time or timestamp.</summary>
    public TimeUnit? Unit { get; init; }

    /// <summary>Whether a time or timestamp is in UTC, as opposed to a reading of the clock on the wall.</summary>
    public bool? IsAdjustedToUtc { get; init; }

    /// <summary>For a byte array: whether it holds geographies (true) or geometries (false) in Well-Known Binary; null for plain bytes.</summary>
    public bool? IsGeography { get; init; }

    /// <summary>The coordinate reference system of geometries or geographies; null for the default, <c>OGC:CRS84</c>.</summary>
    public string? Crs { get; init; }

    /// <summary>How the edges of geographies are interpolated; null for the default, <see cref="EdgeInterpolationAlgorithm.Spherical"/>.</summary>
    public EdgeInterpolationAlgorithm? Algorithm { get; init; }
}
