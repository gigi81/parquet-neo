namespace ParquetNeo.Filtering;

/// <summary>What the statistics of a row group say about the rows that satisfy a filter. The values are ordered so that negating a filter is <c>2 - outcome</c>.</summary>
internal enum FilterOutcome
{
    /// <summary>No row can satisfy the filter: the row group can be skipped.</summary>
    None = 0,

    /// <summary>The statistics cannot tell.</summary>
    Some = 1,

    /// <summary>Every row satisfies the filter.</summary>
    All = 2,
}
