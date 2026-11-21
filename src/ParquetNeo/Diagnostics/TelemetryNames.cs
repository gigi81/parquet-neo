namespace ParquetNeo.Diagnostics;

/// <summary>
/// The names that the metrics (<see cref="ParquetNeoMetrics"/>) and the traces (<see cref="ParquetNeoTracing"/>) share, in one place: the name of the meter and of
/// the source, the prefixes of the names of what is measured, and the tags that both put on what they record, which are the same tags so that a metric and a span can
/// be looked at together. Each one that is public is a constant of the class that uses it; this class is the only place where it is written.
/// </summary>
internal static class TelemetryNames
{
    /// <summary>The name of the meter and of the activity source.</summary>
    public const string Name = "ParquetNeo";

    /// <summary>What every name that ParquetNeo records starts with.</summary>
    public const string Root = "parquetneo";

    /// <summary>The names about a storage.</summary>
    public const string Storage = Root + ".storage";

    /// <summary>The names about the queries of a dataset.</summary>
    public const string Query = Root + ".query";

    /// <summary>The names about a row group, for a span (the metrics say <c>row_groups</c>).</summary>
    public const string RowGroup = Root + ".row_group";

    /// <summary>The tag with the kind of storage: <c>aws_s3</c>, <c>azure_blob</c>, or what a storage says it is.</summary>
    public const string StorageSystemTag = Storage + ".system";

    /// <summary>The tag of a query with whether a condition was used to skip files and row groups.</summary>
    public const string QueryFilteredTag = Query + ".filtered";

    /// <summary>The rows that a query read from the files: a tag of the span of the query, and the name of the counter.</summary>
    public const string QueryRows = Query + ".rows";

    /// <summary>The tag of the spans that ended with an exception, with the type of it, as the conventions of OpenTelemetry name it.</summary>
    public const string ErrorTypeTag = "error.type";
}
