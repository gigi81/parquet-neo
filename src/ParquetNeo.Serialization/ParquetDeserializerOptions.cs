using ParquetNeo.Filtering;

namespace ParquetNeo.Serialization;

/// <summary>Settings for reading rows with <see cref="ParquetSerializer"/>.</summary>
public sealed record ParquetDeserializerOptions
{
    /// <summary>The codecs to decompress with, and the limits that protect against corrupt files; the defaults when not set.</summary>
    public ParquetReaderOptions Reader { get; init; } = new();

    /// <summary>
    /// A condition that row groups are checked against by the statistics of their columns, to skip those that cannot hold a row that
    /// satisfies it: they are not read (with the asynchronous methods, their bytes are not fetched either). It only prunes: the rows
    /// of the row groups that are read are all returned, whether they satisfy the condition or not, so filter them as well when the
    /// result must hold exactly. All the row groups are read when it is not set.
    /// </summary>
    public ParquetFilter? RowGroupFilter { get; init; }
}
