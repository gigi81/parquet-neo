using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ParquetNeo.Diagnostics;

/// <summary>
/// The metrics of ParquetNeo, as <see cref="System.Diagnostics.Metrics"/> instruments of a meter called <see cref="MeterName"/>: the requests made
/// to a storage and what they read, the row groups that are read and skipped, and the datasets queries. Give an instance to the options of what
/// you want to measure (<see cref="ParquetReaderOptions.Metrics"/>, <see cref="Storage.RangeReadStreamOptions.Metrics"/>, and the options of a
/// dataset); anything that is not given one records nothing. Nothing is exported by itself: a listener (OpenTelemetry, <c>dotnet-counters</c>, a
/// <see cref="MeterListener"/>) that subscribes to the meter receives the measurements, and with no listener recording is nearly free.
/// </summary>
/// <remarks>
/// In an application that uses dependency injection, <c>AddParquetNeoMetrics()</c> of the package <c>ParquetNeo.Extensions.DependencyInjection</c> makes one
/// that takes its meter from the <see cref="IMeterFactory"/> of the container. The names follow the conventions of OpenTelemetry. Safe to use from
/// several threads.
/// </remarks>
public sealed class ParquetNeoMetrics : IDisposable
{
    /// <summary>The name of the meter, to subscribe to (<c>AddMeter(ParquetNeoMetrics.MeterName)</c> in OpenTelemetry).</summary>
    public const string MeterName = "ParquetNeo";

    /// <summary>The tag of the storage requests and objects with the kind of storage: <c>aws_s3</c>, <c>azure_blob</c>, or what a storage says it is.</summary>
    public const string StorageSystemTag = "parquetneo.storage.system";

    /// <summary>The tag of the storage requests with how they ended: <c>ok</c> or <c>error</c>.</summary>
    public const string OutcomeTag = "parquetneo.outcome";

    /// <summary>The tag of the queries of a dataset with whether a condition was used to skip files and row groups.</summary>
    public const string FilteredTag = "parquetneo.query.filtered";

    private readonly Meter _meter;
    private readonly bool _ownsMeter;
    private readonly Counter<long> _requests;
    private readonly Counter<long> _bytesRead;
    private readonly Histogram<double> _requestDuration;
    private readonly Counter<long> _objectsOpened;
    private readonly Counter<long> _listings;
    private readonly Counter<long> _rowGroupsRead;
    private readonly Counter<long> _rowGroupsSkipped;
    private readonly Counter<long> _rowsSkipped;
    private readonly Histogram<double> _queryDuration;
    private readonly Counter<long> _queryRows;

    /// <param name="meterFactory">
    /// Where the meter comes from, as the container of dependency injection offers it; a factory owns its meters and tags them for the container
    /// they belong to. Without one, the meter is made here and is disposed with these metrics.
    /// </param>
    public ParquetNeoMetrics(IMeterFactory? meterFactory = null)
    {
        string version = typeof(ParquetNeoMetrics).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        _ownsMeter = meterFactory is null;
        _meter = meterFactory?.Create(MeterName, version) ?? new Meter(MeterName, version);

        _requests = _meter.CreateCounter<long>("parquetneo.storage.requests", "{request}", "Requests made to a storage for a range of an object.");
        _bytesRead = _meter.CreateCounter<long>("parquetneo.storage.bytes_read", "By", "Bytes received from a storage.");
        _requestDuration = _meter.CreateHistogram<double>("parquetneo.storage.request.duration", "s", "How long a request to a storage took.");
        _objectsOpened = _meter.CreateCounter<long>("parquetneo.storage.objects_opened", "{object}", "Objects of a storage opened for reading.");
        _listings = _meter.CreateCounter<long>("parquetneo.storage.listings", "{listing}", "Listings of the objects of a prefix.");
        _rowGroupsRead = _meter.CreateCounter<long>("parquetneo.row_groups.read", "{row_group}", "Row groups that were read.");
        _rowGroupsSkipped = _meter.CreateCounter<long>("parquetneo.row_groups.skipped", "{row_group}", "Row groups that a filter ruled out, by their statistics or their bloom filters, so they were not read.");
        _rowsSkipped = _meter.CreateCounter<long>("parquetneo.rows.skipped", "{row}", "Rows of the row groups that were read but not decoded, because the page index of the file ruled out their pages.");
        _queryDuration = _meter.CreateHistogram<double>("parquetneo.query.duration", "s", "How long a query of a dataset took, from the first row asked for to the last one given.");
        _queryRows = _meter.CreateCounter<long>("parquetneo.query.rows", "{row}", "Rows that the queries of a dataset read from the files, before the operators of the query.");
    }

    /// <summary>The meter of these metrics, for code that listens to exactly these metrics and not to every meter of the name (a test, for example).</summary>
    public Meter Meter => _meter;

    /// <summary>Stops the meter, when these metrics made it; a meter of a factory is the factory's.</summary>
    public void Dispose()
    {
        if (_ownsMeter)
        {
            _meter.Dispose();
        }
    }

    /// <summary>Records a request to a storage for a range of an object, for storages that are not made with <see cref="Storage.RangeReadStream"/>.</summary>
    /// <param name="system">The kind of storage (the value of <see cref="StorageSystemTag"/>).</param>
    /// <param name="bytes">How many bytes it received.</param>
    /// <param name="seconds">How long it took.</param>
    /// <param name="succeeded">Whether it succeeded.</param>
    public void RecordStorageRequest(string system, long bytes, double seconds, bool succeeded)
    {
        var tags = new TagList { { StorageSystemTag, system }, { OutcomeTag, succeeded ? "ok" : "error" } };
        _requests.Add(1, tags);
        _requestDuration.Record(seconds, tags);
        if (succeeded && bytes > 0)
        {
            _bytesRead.Add(bytes, new KeyValuePair<string, object?>(StorageSystemTag, system));
        }
    }

    /// <summary>Records that an object of a storage of the kind <paramref name="system"/> was opened for reading.</summary>
    public void RecordObjectOpened(string system) => _objectsOpened.Add(1, new KeyValuePair<string, object?>(StorageSystemTag, system));

    /// <summary>Records that the objects of a prefix of a storage of the kind <paramref name="system"/> were listed.</summary>
    public void RecordListing(string system) => _listings.Add(1, new KeyValuePair<string, object?>(StorageSystemTag, system));

    /// <summary>Records the row groups of a file that are going to be read, and the ones a filter ruled out.</summary>
    public void RecordRowGroups(long read, long skipped)
    {
        _rowGroupsRead.Add(read);
        if (skipped > 0)
        {
            _rowGroupsSkipped.Add(skipped);
        }
    }

    /// <summary>Records rows that the page index of a file ruled out.</summary>
    public void RecordRowsSkipped(long rows)
    {
        if (rows > 0)
        {
            _rowsSkipped.Add(rows);
        }
    }

    /// <summary>Records a query of a dataset: how long it took, and how many rows it read from the files.</summary>
    public void RecordQuery(double seconds, long rows, bool filtered)
    {
        var tag = new KeyValuePair<string, object?>(FilteredTag, filtered);
        _queryDuration.Record(seconds, tag);
        _queryRows.Add(rows, tag);
    }

    /// <summary>Seconds since a moment taken with <see cref="Stopwatch.GetTimestamp"/>.</summary>
    internal static double SecondsSince(long timestamp) => Stopwatch.GetElapsedTime(timestamp).TotalSeconds;
}
