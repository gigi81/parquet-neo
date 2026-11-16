using ParquetNeo.Data;
using ParquetNeo.Diagnostics;
using ParquetNeo.Storage;
using ParquetNeo.Tests.Storage;

namespace ParquetNeo.Tests.Diagnostics;

/// <summary>What the instrumented code of the core records into <see cref="ParquetNeoMetrics"/>.</summary>
public class MetricsTests
{
    private const string System = ParquetNeoMetrics.StorageSystemTag;

    [Test]
    public async Task TheInstruments_HaveTheNamesAndUnitsOfTheirKind()
    {
        using var metrics = new ParquetNeoMetrics();
        var seen = new Dictionary<string, string?>();
        using var listener = new System.Diagnostics.Metrics.MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == ParquetNeoMetrics.MeterName)
                {
                    seen[instrument.Name] = instrument.Unit;
                }
            },
        };
        listener.Start();

        await Assert.That(seen["parquetneo.storage.requests"]).IsEqualTo("{request}");
        await Assert.That(seen["parquetneo.storage.bytes_read"]).IsEqualTo("By");
        await Assert.That(seen["parquetneo.storage.request.duration"]).IsEqualTo("s");
        await Assert.That(seen["parquetneo.query.duration"]).IsEqualTo("s");
        await Assert.That(seen.Keys).Contains("parquetneo.row_groups.read");
        await Assert.That(seen.Keys).Contains("parquetneo.row_groups.skipped");
        await Assert.That(seen.Keys).Contains("parquetneo.rows.skipped");
        await Assert.That(seen.Keys).Contains("parquetneo.storage.objects_opened");
        await Assert.That(seen.Keys).Contains("parquetneo.storage.listings");
        await Assert.That(seen.Count).IsEqualTo(10);
    }

    [Test]
    public async Task TheRequestsOfAStream_AreCountedTimedAndSized_AsTheStorageSawThem()
    {
        using var metrics = new ParquetNeoMetrics();
        using var recorder = new MetricRecorder(metrics.Meter);
        var storage = new InMemoryStorage { Options = new RangeReadStreamOptions { BlockSize = 1000, TailSize = 0, Metrics = metrics } }
            .Put("a", new byte[10_000]);

        await using (var stream = await storage.OpenReadAsync("a"))
        {
            await stream.ReadExactlyAsync(new byte[10]);
            stream.Position = 5000;
            await stream.ReadExactlyAsync(new byte[3000]);
        }

        await Assert.That(recorder.Sum("parquetneo.storage.requests")).IsEqualTo(storage.Requests.Count);
        await Assert.That(recorder.Sum("parquetneo.storage.bytes_read")).IsEqualTo(storage.BytesRead);
        await Assert.That(recorder.Count("parquetneo.storage.request.duration")).IsEqualTo(2);
        await Assert.That(recorder.Of("parquetneo.storage.request.duration").All(m => m.Value >= 0)).IsTrue();
        await Assert.That(recorder.Sum("parquetneo.storage.requests", (System, "memory"), (ParquetNeoMetrics.OutcomeTag, "ok"))).IsEqualTo(2);
        await Assert.That(recorder.Sum("parquetneo.storage.bytes_read", (System, "memory"))).IsEqualTo(4000);
    }

    [Test]
    public async Task ARequestThatFails_IsCountedAsAnError_AndGotNoBytes()
    {
        using var metrics = new ParquetNeoMetrics();
        using var recorder = new MetricRecorder(metrics.Meter);
        await using var stream = new FailingStream(metrics);

        await Assert.That(async () => await stream.ReadAsync(new byte[10])).Throws<InvalidOperationException>();

        await Assert.That(recorder.Sum("parquetneo.storage.requests", (ParquetNeoMetrics.OutcomeTag, "error"))).IsEqualTo(1);
        await Assert.That(recorder.Sum("parquetneo.storage.requests", (ParquetNeoMetrics.OutcomeTag, "ok"))).IsEqualTo(0);
        await Assert.That(recorder.Sum("parquetneo.storage.bytes_read")).IsEqualTo(0);
        await Assert.That(recorder.Count("parquetneo.storage.request.duration")).IsEqualTo(1);
        await Assert.That(recorder.Of("parquetneo.storage.requests").Single().Tags[System]).IsEqualTo("other");
    }

    [Test]
    public async Task WithoutMetrics_NothingIsRecorded_AndTheStreamWorks()
    {
        using var metrics = new ParquetNeoMetrics();
        using var recorder = new MetricRecorder(metrics.Meter);
        var storage = new InMemoryStorage { Options = new RangeReadStreamOptions { BlockSize = 1000, TailSize = 0 } }.Put("a", new byte[3000]);

        await using var stream = await storage.OpenReadAsync("a");
        await stream.ReadExactlyAsync(new byte[2000]);

        await Assert.That(recorder.All.Count).IsEqualTo(0);
    }

    [Test]
    public async Task RowGroupsReadAndSkipped_AreCountedByTheAdoNetReader()
    {
        using var metrics = new ParquetNeoMetrics();
        using var recorder = new MetricRecorder(metrics.Meter);
        var file = StorageSampleFile.Create(rowGroups: 10, rowsPerGroup: 500);

        using (var reader = ParquetDataReader.Open(new MemoryStream(file), new ParquetDataReaderOptions
        {
            Reader = new ParquetReaderOptions { Metrics = metrics },
            RowGroupFilter = ParquetFilter.Column("Id").GreaterThanOrEqual(3000) & ParquetFilter.Column("Id").LessThan(4000),
        }))
        {
            int rows = 0;
            while (reader.Read())
            {
                rows++;
            }

            await Assert.That(rows).IsLessThan(2000);
        }

        await Assert.That(recorder.Sum("parquetneo.row_groups.read")).IsEqualTo(2);
        await Assert.That(recorder.Sum("parquetneo.row_groups.skipped")).IsEqualTo(8);
        await Assert.That(recorder.Sum("parquetneo.rows.skipped")).IsGreaterThanOrEqualTo(0);
    }

    [Test]
    public async Task RowsThatThePageIndexSkips_AreCounted()
    {
        using var metrics = new ParquetNeoMetrics();
        using var recorder = new MetricRecorder(metrics.Meter);
        var file = StorageSampleFile.Create(rowGroups: 1, rowsPerGroup: 20_000);

        using var reader = ParquetFileReader.Open(new MemoryStream(file), new ParquetReaderOptions { Metrics = metrics });
        var group = reader.OpenRowGroup(0);
        var selection = group.SelectRows(ParquetFilter.Column("Id").Equal(12_345), [0, 1])!;

        await Assert.That(recorder.Sum("parquetneo.rows.skipped")).IsEqualTo(20_000 - selection.RowCount);
        await Assert.That(recorder.Sum("parquetneo.rows.skipped")).IsGreaterThan(10_000);
    }

    [Test]
    public async Task TheMetricsOfAFactory_AreTheFactorysToDispose_AndTheOnesMadeHereAreDisposedWithThem()
    {
        var factory = new CountingMeterFactory();
        var fromFactory = new ParquetNeoMetrics(factory);
        fromFactory.Dispose();
        var owned = new ParquetNeoMetrics();
        using var recorder = new MetricRecorder(owned.Meter);
        owned.RecordObjectOpened("x");
        owned.Dispose();
        owned.RecordObjectOpened("y");

        await Assert.That(factory.Created).IsEqualTo(1);
        await Assert.That(factory.Meter!.Name).IsEqualTo(ParquetNeoMetrics.MeterName);
        await Assert.That(recorder.Count("parquetneo.storage.objects_opened")).IsEqualTo(1);
        factory.Dispose();
    }

    private sealed class CountingMeterFactory : System.Diagnostics.Metrics.IMeterFactory
    {
        public int Created { get; private set; }

        public System.Diagnostics.Metrics.Meter? Meter { get; private set; }

        public System.Diagnostics.Metrics.Meter Create(System.Diagnostics.Metrics.MeterOptions options)
        {
            Created++;
            return Meter = new System.Diagnostics.Metrics.Meter(options);
        }

        public void Dispose() => Meter?.Dispose();
    }

    /// <summary>A stream whose storage fails.</summary>
    private sealed class FailingStream(ParquetNeoMetrics metrics) : RangeReadStream(100, new RangeReadStreamOptions { TailSize = 0, Metrics = metrics })
    {
        protected override ValueTask<int> ReadRangeAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the storage is down");
    }
}
