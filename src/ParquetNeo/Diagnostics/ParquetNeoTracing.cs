using System.Diagnostics;

namespace ParquetNeo.Diagnostics;

/// <summary>
/// The traces of ParquetNeo, as the <see cref="Activity"/> spans of an <see cref="ActivitySource"/> called <see cref="SourceName"/>: the requests made to
/// a storage, the objects it opens and lists, the row groups that are read and the queries of a dataset. Give an instance to the options of what you
/// want to trace (<see cref="ParquetReaderOptions.Tracing"/>, <see cref="Storage.RangeReadStreamOptions.Tracing"/>, and the options of a dataset);
/// anything that is not given one records nothing. Nothing is exported by itself: a listener (OpenTelemetry, an <see cref="ActivityListener"/>) that
/// subscribes to the source receives the spans, and with no listener a span is not even created.
/// </summary>
/// <remarks>
/// The spans are children of the span that is current where the work is done, so a query of a dataset holds the row groups and the requests it
/// causes, and your own spans hold the query. The tags follow the metrics (<see cref="ParquetNeoMetrics"/>). Safe to use from several threads.
/// </remarks>
public sealed class ParquetNeoTracing : IDisposable
{
    /// <summary>The name of the source, to subscribe to (<c>AddSource(ParquetNeoTracing.SourceName)</c> in OpenTelemetry).</summary>
    public const string SourceName = TelemetryNames.Name;

    /// <summary>The name of the span of a request to a storage for a range of an object.</summary>
    public const string StorageRequestSpan = TelemetryNames.Storage + ".request";

    /// <summary>The name of the span of the opening of an object of a storage for reading.</summary>
    public const string StorageOpenSpan = TelemetryNames.Storage + ".open";

    /// <summary>The name of the span of the listing of the objects of a prefix of a storage.</summary>
    public const string StorageListSpan = TelemetryNames.Storage + ".list";

    /// <summary>The name of the span of the reading of a row group.</summary>
    public const string RowGroupSpan = TelemetryNames.RowGroup + ".read";

    /// <summary>The name of the span of a query of a dataset.</summary>
    public const string QuerySpan = TelemetryNames.Query;

    /// <summary>The tag with the kind of storage: the same as <see cref="ParquetNeoMetrics.StorageSystemTag"/>.</summary>
    public const string StorageSystemTag = TelemetryNames.StorageSystemTag;

    /// <summary>The tag of the spans of a storage with the key of the object, or the prefix that is listed.</summary>
    public const string StorageKeyTag = TelemetryNames.Storage + ".key";

    /// <summary>The tag of a request with where in the object the range starts.</summary>
    public const string StorageOffsetTag = TelemetryNames.Storage + ".offset";

    /// <summary>The tag of a request with the bytes that were asked for first, and that it received when it ended.</summary>
    public const string StorageBytesTag = TelemetryNames.Storage + ".bytes";

    /// <summary>The tag of a row group with its position in the file.</summary>
    public const string RowGroupIndexTag = TelemetryNames.RowGroup + ".index";

    /// <summary>The tag of a row group with the rows it has, after the page index has ruled out some.</summary>
    public const string RowGroupRowsTag = TelemetryNames.RowGroup + ".rows";

    /// <summary>The tag of a query with whether a condition was used to skip files and row groups: the same as <see cref="ParquetNeoMetrics.FilteredTag"/>.</summary>
    public const string FilteredTag = TelemetryNames.QueryFilteredTag;

    /// <summary>The tag of a query with the rows it read from the files, set when it ends.</summary>
    public const string QueryRowsTag = TelemetryNames.QueryRows;

    private readonly ActivitySource _source;

    /// <summary>Makes the source, which is disposed with this object.</summary>
    public ParquetNeoTracing()
    {
        string version = typeof(ParquetNeoTracing).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        _source = new ActivitySource(SourceName, version);
    }

    /// <summary>The source of these traces, for code that listens to exactly these traces and not to every source of the name (a test, for example).</summary>
    public ActivitySource Source => _source;

    /// <summary>Stops the source.</summary>
    public void Dispose() => _source.Dispose();

    /// <summary>Starts the span of a request to a storage for a range of an object; null when nothing listens.</summary>
    /// <param name="system">The kind of storage (the value of <see cref="StorageSystemTag"/>).</param>
    /// <param name="offset">Where the range starts.</param>
    /// <param name="length">How many bytes are asked for.</param>
    public Activity? StartStorageRequest(string system, long offset, int length)
    {
        var activity = _source.StartActivity(StorageRequestSpan, ActivityKind.Client);
        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag(StorageSystemTag, system);
            activity.SetTag(StorageOffsetTag, offset);
            activity.SetTag(StorageBytesTag, length);
        }

        return activity;
    }

    /// <summary>Starts the span of the opening of an object of a storage for reading; null when nothing listens.</summary>
    public Activity? StartStorageOpen(string system, string key) => StartStorage(StorageOpenSpan, system, key);

    /// <summary>Starts the span of the listing of the objects of a prefix of a storage; null when nothing listens.</summary>
    public Activity? StartStorageList(string system, string prefix) => StartStorage(StorageListSpan, system, prefix);

    /// <summary>Starts the span of the reading of a row group; null when nothing listens.</summary>
    public Activity? StartRowGroup(int index)
    {
        var activity = _source.StartActivity(RowGroupSpan);
        activity?.SetTag(RowGroupIndexTag, index);
        return activity;
    }

    /// <summary>
    /// Starts the span of a query of a dataset; null when nothing listens. It is not left as the current span: the code that asks for the rows makes it
    /// current while it does, so that the work for a row is below it, and the code that holds the rows goes on with its own span.
    /// </summary>
    public Activity? StartQuery(bool filtered)
    {
        var parent = Activity.Current;
        var activity = _source.StartActivity(QuerySpan);
        if (activity is not null)
        {
            activity.SetTag(FilteredTag, filtered);

            // The query is held by the code that asks for rows, which goes on with its own span between two rows.
            Activity.Current = parent;
        }

        return activity;
    }

    /// <summary>Marks a span as failed with the exception, which is not thrown again.</summary>
    public static void RecordException(Activity? activity, Exception exception)
    {
        if (activity is null)
        {
            return;
        }

        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        activity.SetTag(TelemetryNames.ErrorTypeTag, exception.GetType().FullName);
    }

    private Activity? StartStorage(string name, string system, string key)
    {
        var activity = _source.StartActivity(name, ActivityKind.Client);
        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag(StorageSystemTag, system);
            activity.SetTag(StorageKeyTag, key);
        }

        return activity;
    }
}
