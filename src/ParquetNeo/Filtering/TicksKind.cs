namespace ParquetNeo.Filtering;

/// <summary>What a count of units in the statistics of a column is a count of.</summary>
internal enum TicksKind
{
    /// <summary>Days since 1970-01-01.</summary>
    Date,

    /// <summary>Units since midnight.</summary>
    Time,

    /// <summary>Units since 1970-01-01 00:00:00.</summary>
    Timestamp,
}
