using System.Diagnostics.Metrics;

namespace ParquetNeo.Tests.Diagnostics;

/// <summary>A measurement that was recorded: the instrument, the value and the tags.</summary>
public sealed record RecordedMeasurement(string Instrument, double Value, IReadOnlyDictionary<string, object?> Tags);

/// <summary>Listens to a meter and keeps what it records, to check what instrumented code measures.</summary>
public sealed class MetricRecorder : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly List<RecordedMeasurement> _measurements = [];
    private readonly Lock _lock = new();

    /// <param name="meter">The meter to listen to: this one, and not every meter of its name, which other tests running at the same time have.</param>
    public MetricRecorder(Meter meter)
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter == meter)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.Start();
    }

    /// <summary>Everything that was recorded, in order.</summary>
    public IReadOnlyList<RecordedMeasurement> All
    {
        get
        {
            lock (_lock)
            {
                return [.. _measurements];
            }
        }
    }

    /// <summary>The measurements of an instrument.</summary>
    public IReadOnlyList<RecordedMeasurement> Of(string instrument) => All.Where(m => m.Instrument == instrument).ToList();

    /// <summary>The sum of the values of an instrument, for the measurements that have the tags given (all of them when none are).</summary>
    public double Sum(string instrument, params (string Key, object? Value)[] tags) =>
        Of(instrument).Where(m => tags.All(tag => m.Tags.TryGetValue(tag.Key, out var value) && Equals(value, tag.Value))).Sum(m => m.Value);

    /// <summary>How many measurements an instrument recorded (for a histogram, how many values).</summary>
    public int Count(string instrument, params (string Key, object? Value)[] tags) =>
        Of(instrument).Count(m => tags.All(tag => m.Tags.TryGetValue(tag.Key, out var value) && Equals(value, tag.Value)));

    public void Dispose() => _listener.Dispose();

    private void Add(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        var copy = tags.ToArray().ToDictionary(pair => pair.Key, pair => pair.Value);
        lock (_lock)
        {
            _measurements.Add(new RecordedMeasurement(instrument.Name, value, copy));
        }
    }
}
