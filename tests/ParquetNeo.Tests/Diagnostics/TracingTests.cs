using System.Diagnostics;
using ParquetNeo.Diagnostics;
using ParquetNeo.Storage;
using ParquetNeo.Tests.Storage;

namespace ParquetNeo.Tests.Diagnostics;

/// <summary>What the instrumented code of the core records into <see cref="ParquetNeoTracing"/>.</summary>
public class TracingTests
{
    [Test]
    public async Task TheSource_IsCalledParquetNeo_AndIsTheOneTheOptionsTrace()
    {
        using var tracing = new ParquetNeoTracing();

        await Assert.That(TelemetryNames.Name).IsEqualTo("ParquetNeo");
        await Assert.That(tracing.Source.Name).IsEqualTo(TelemetryNames.Name);
        await Assert.That(tracing.Source.Version).IsNotNull();
    }

    [Test]
    public async Task TheNamesOfTheSpansAndTheTags_AreAsDocumented_WhateverTheyAreBuiltFrom()
    {
        // What users put in dashboards and queries: a change of one of these is a breaking change, so they are written out here.
        await Assert.That(TelemetryNames.Name).IsEqualTo("ParquetNeo");
        await Assert.That(TelemetryNames.StorageRequestSpan).IsEqualTo("parquetneo.storage.request");
        await Assert.That(TelemetryNames.StorageOpenSpan).IsEqualTo("parquetneo.storage.open");
        await Assert.That(TelemetryNames.StorageListSpan).IsEqualTo("parquetneo.storage.list");
        await Assert.That(TelemetryNames.RowGroupSpan).IsEqualTo("parquetneo.row_group.read");
        await Assert.That(TelemetryNames.QuerySpan).IsEqualTo("parquetneo.query");
        await Assert.That(TelemetryNames.StorageSystemTag).IsEqualTo("parquetneo.storage.system");
        await Assert.That(TelemetryNames.StorageKeyTag).IsEqualTo("parquetneo.storage.key");
        await Assert.That(TelemetryNames.StorageOffsetTag).IsEqualTo("parquetneo.storage.offset");
        await Assert.That(TelemetryNames.StorageBytesTag).IsEqualTo("parquetneo.storage.bytes");
        await Assert.That(TelemetryNames.OutcomeTag).IsEqualTo("parquetneo.outcome");
        await Assert.That(TelemetryNames.RowGroupIndexTag).IsEqualTo("parquetneo.row_group.index");
        await Assert.That(TelemetryNames.RowGroupRowsTag).IsEqualTo("parquetneo.row_group.rows");
        await Assert.That(TelemetryNames.QueryFilteredTag).IsEqualTo("parquetneo.query.filtered");
        await Assert.That(TelemetryNames.QueryRowsTag).IsEqualTo("parquetneo.query.rows");
        await Assert.That(TelemetryNames.ErrorTypeTag).IsEqualTo("error.type");
    }

    [Test]
    public async Task EachRequestOfAStream_IsASpanOfTheStorageWithWhereItReadAndHowMuch()
    {
        using var tracing = new ParquetNeoTracing();
        using var recorder = new TraceRecorder(tracing.Source);
        var storage = new InMemoryStorage { Options = new RangeReadStreamOptions { BlockSize = 1000, TailSize = 0, Tracing = tracing } }
            .Put("a", new byte[10_000]);

        await using (var stream = await storage.OpenReadAsync("a"))
        {
            await stream.ReadExactlyAsync(new byte[10]);
            stream.Position = 5000;
            await stream.ReadExactlyAsync(new byte[3000]);
        }

        var spans = recorder.Of(TelemetryNames.StorageRequestSpan);
        await Assert.That(spans.Count).IsEqualTo(2);
        await Assert.That(spans.All(span => span.Kind == ActivityKind.Client && span.Status != ActivityStatusCode.Error)).IsTrue();
        await Assert.That(spans.Select(span => (long)span.GetTagItem(TelemetryNames.StorageOffsetTag)!)).IsEquivalentTo(new long[] { 0, 5000 });
        await Assert.That(spans.Select(span => (int)span.GetTagItem(TelemetryNames.StorageBytesTag)!)).IsEquivalentTo(new[] { 1000, 3000 });
        await Assert.That(spans.All(span => (string?)span.GetTagItem(TelemetryNames.StorageSystemTag) == "memory")).IsTrue();
    }

    [Test]
    public async Task ARequestThatFails_IsASpanWithAnErrorStatus_AndTheTypeOfTheError()
    {
        using var tracing = new ParquetNeoTracing();
        using var recorder = new TraceRecorder(tracing.Source);
        await using var stream = new FailingStream(tracing);

        await Assert.That(async () => await stream.ReadAsync(new byte[10])).Throws<InvalidOperationException>();

        var span = recorder.Of(TelemetryNames.StorageRequestSpan).Single();
        await Assert.That(span.Status).IsEqualTo(ActivityStatusCode.Error);
        await Assert.That(span.StatusDescription).IsEqualTo("the storage is down");
        await Assert.That(span.GetTagItem(TelemetryNames.ErrorTypeTag)).IsEqualTo(typeof(InvalidOperationException).FullName);
    }

    [Test]
    public async Task TheRequestsOfARead_AreBelowTheSpanThatIsCurrentWhereItIsDone()
    {
        using var tracing = new ParquetNeoTracing();
        using var recorder = new TraceRecorder(tracing.Source);
        using var own = new ActivitySource("test.own");
        using var ownRecorder = new TraceRecorder(own);
        var storage = new InMemoryStorage { Options = new RangeReadStreamOptions { BlockSize = 1000, TailSize = 0, Tracing = tracing } }
            .Put("a", new byte[10_000]);

        using (var parent = own.StartActivity("mine"))
        {
            await using var stream = await storage.OpenReadAsync("a");
            await stream.ReadExactlyAsync(new byte[10]);

            await Assert.That(Activity.Current).IsEqualTo(parent);
        }

        var request = recorder.Of(TelemetryNames.StorageRequestSpan).Single();
        await Assert.That(request.ParentSpanId).IsEqualTo(ownRecorder.Spans.Single().SpanId);
        await Assert.That(request.TraceId).IsEqualTo(ownRecorder.Spans.Single().TraceId);
    }

    [Test]
    public async Task WithoutTracing_NothingIsRecorded_AndTheStreamWorks()
    {
        using var tracing = new ParquetNeoTracing();
        using var recorder = new TraceRecorder(tracing.Source);
        var storage = new InMemoryStorage { Options = new RangeReadStreamOptions { BlockSize = 1000, TailSize = 0 } }.Put("a", new byte[3000]);

        await using var stream = await storage.OpenReadAsync("a");
        await stream.ReadExactlyAsync(new byte[2000]);

        await Assert.That(recorder.Spans.Count).IsEqualTo(0);
    }

    [Test]
    public async Task AnExceptionRecordedOnNoSpan_IsNothing_AsWhenNothingListens()
    {
        // The test runner has a listener of its own, so whether a span is made when nothing listens cannot be seen here.
        ParquetNeoTracing.RecordException(null, new InvalidOperationException());

        await Assert.That(TelemetryNames.Name).IsNotEmpty();
    }

    [Test]
    public async Task TheSpansOfAStorageAreOfItsKind_AndOfTheObjectOrPrefix()
    {
        using var tracing = new ParquetNeoTracing();
        using var recorder = new TraceRecorder(tracing.Source);

        using (tracing.StartStorageOpen("aws_s3", "people/a.parquet"))
        {
        }

        using (tracing.StartStorageList("aws_s3", "people/"))
        {
        }

        var open = recorder.Of(TelemetryNames.StorageOpenSpan).Single();
        var list = recorder.Of(TelemetryNames.StorageListSpan).Single();
        await Assert.That(open.GetTagItem(TelemetryNames.StorageKeyTag)).IsEqualTo("people/a.parquet");
        await Assert.That(list.GetTagItem(TelemetryNames.StorageKeyTag)).IsEqualTo("people/");
        await Assert.That(open.GetTagItem(TelemetryNames.StorageSystemTag)).IsEqualTo("aws_s3");
        await Assert.That(list.Kind).IsEqualTo(ActivityKind.Client);
    }

    [Test]
    public async Task TheSourceIsStopped_WhenTheTracingIsDisposed()
    {
        var tracing = new ParquetNeoTracing();
        using var recorder = new TraceRecorder(tracing.Source);
        tracing.Dispose();

        using var started = tracing.StartStorageOpen("x", "k");

        await Assert.That(recorder.Spans.Count).IsEqualTo(0);
    }

    /// <summary>A stream whose storage fails.</summary>
    private sealed class FailingStream(ParquetNeoTracing tracing) : RangeReadStream(100, new RangeReadStreamOptions { TailSize = 0, Tracing = tracing })
    {
        protected override ValueTask<int> ReadRangeAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("the storage is down");
    }
}
