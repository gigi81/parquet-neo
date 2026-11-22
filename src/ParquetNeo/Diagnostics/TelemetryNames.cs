namespace ParquetNeo.Diagnostics;

/// <summary>
/// The names that ParquetNeo records, in one place, for the metrics (<see cref="ParquetNeoMetrics"/>) and the traces (<see cref="ParquetNeoTracing"/>): the name of
/// the meter and of the activity source, the names of the spans, and the tags. Use them to subscribe to what ParquetNeo records
/// (<c>AddSource(TelemetryNames.Name)</c> and <c>AddMeter(TelemetryNames.Name)</c> in OpenTelemetry) and to read the tags of what you receive. The metrics and the spans
/// that are about the same thing use the same tags, so that a metric and a span can be looked at together.
/// </summary>
public static class TelemetryNames
{
    /// <summary>What every name that ParquetNeo records starts with.</summary>
    internal const string Root = "parquetneo";

    /// <summary>The start of the names about a storage.</summary>
    internal const string Storage = Root + ".storage";

    /// <summary>The start of the names about the queries of a dataset.</summary>
    internal const string Query = Root + ".query";

    /// <summary>The start of the names of the spans about a row group (the metrics say <c>row_groups</c>).</summary>
    internal const string RowGroup = Root + ".row_group";

    /// <summary>The name of the meter of the metrics and of the activity source of the traces, to subscribe to.</summary>
    public const string Name = "ParquetNeo";

    /// <summary>The name of the span of a request to a storage for a range of an object.</summary>
    public const string StorageRequestSpan = Storage + ".request";

    /// <summary>The name of the span of the opening of an object of a storage for reading.</summary>
    public const string StorageOpenSpan = Storage + ".open";

    /// <summary>The name of the span of the listing of the objects of a prefix of a storage.</summary>
    public const string StorageListSpan = Storage + ".list";

    /// <summary>The name of the span of the reading of a row group.</summary>
    public const string RowGroupSpan = RowGroup + ".read";

    /// <summary>The name of the span of a query of a dataset.</summary>
    public const string QuerySpan = Query;

    /// <summary>The tag of the storage requests, objects and spans with the kind of storage: <c>aws_s3</c>, <c>azure_blob</c>, or what a storage says it is.</summary>
    public const string StorageSystemTag = Storage + ".system";

    /// <summary>The tag of the spans of a storage with the key of the object, or the prefix that is listed.</summary>
    public const string StorageKeyTag = Storage + ".key";

    /// <summary>The tag of the span of a request with where in the object the range starts.</summary>
    public const string StorageOffsetTag = Storage + ".offset";

    /// <summary>The tag of the span of a request with the bytes that were asked for first, and that it received when it ended.</summary>
    public const string StorageBytesTag = Storage + ".bytes";

    /// <summary>The tag of the storage requests with how they ended: <c>ok</c> or <c>error</c>.</summary>
    public const string OutcomeTag = Root + ".outcome";

    /// <summary>The tag of the span of a row group with its position in the file.</summary>
    public const string RowGroupIndexTag = RowGroup + ".index";

    /// <summary>The tag of the span of a row group with the rows it has, after the page index has ruled out some.</summary>
    public const string RowGroupRowsTag = RowGroup + ".rows";

    /// <summary>The tag of the queries of a dataset, as a metric and as a span, with whether a condition was used to skip files and row groups.</summary>
    public const string QueryFilteredTag = Query + ".filtered";

    /// <summary>The rows that a query read from the files: the tag of the span of the query, set when it ends, and the name of the counter of the metrics.</summary>
    public const string QueryRowsTag = Query + ".rows";

    /// <summary>The tag of the spans that ended with an exception, with the type of it, as the conventions of OpenTelemetry name it.</summary>
    public const string ErrorTypeTag = "error.type";
}
