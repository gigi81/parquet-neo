using System.Diagnostics;

namespace ParquetNeo.Tests.Diagnostics;

/// <summary>Listens to an activity source and keeps the spans that ended, to check what instrumented code traces.</summary>
public sealed class TraceRecorder : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<Activity> _spans = [];
    private readonly Lock _lock = new();

    /// <param name="source">The source to listen to: this one, and not every source of its name, which other tests running at the same time have.</param>
    public TraceRecorder(ActivitySource source)
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = candidate => candidate == source,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (_lock)
                {
                    _spans.Add(activity);
                }
            },
        };
        ActivitySource.AddActivityListener(_listener);
    }

    /// <summary>The spans that ended, in the order they ended.</summary>
    public IReadOnlyList<Activity> Spans
    {
        get
        {
            lock (_lock)
            {
                return [.. _spans];
            }
        }
    }

    /// <summary>The spans of a name that ended.</summary>
    public IReadOnlyList<Activity> Of(string name) => Spans.Where(span => span.OperationName == name).ToList();

    public void Dispose() => _listener.Dispose();
}
