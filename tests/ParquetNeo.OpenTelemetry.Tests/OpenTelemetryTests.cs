using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using ParquetNeo.Diagnostics;
using ParquetNeo.Storage;
using ParquetNeo.Tests.Storage;

namespace ParquetNeo.OpenTelemetry.Tests;

/// <summary>ParquetNeo collected by an OpenTelemetry setup with one call for each of the traces and the metrics.</summary>
public class OpenTelemetryTests
{
    [Test]
    public async Task TheTracerProvider_ExportsTheSpansOfParquetNeo()
    {
        var exported = new List<System.Diagnostics.Activity>();
        using var tracing = new ParquetNeoTracing();
        using (Sdk.CreateTracerProviderBuilder().AddParquetNeoInstrumentation().AddInMemoryExporter(exported).Build())
        {
            var storage = new InMemoryStorage { Options = new RangeReadStreamOptions { BlockSize = 1000, TailSize = 0, Tracing = tracing } }
                .Put("a", new byte[5000]);

            await using var stream = await storage.OpenReadAsync("a");
            await stream.ReadExactlyAsync(new byte[10]);
        }

        await Assert.That(exported.Select(span => span.OperationName)).Contains(ParquetNeoTracing.StorageRequestSpan);
    }

    [Test]
    public async Task TheMeterProvider_ExportsTheMetricsOfParquetNeo()
    {
        var exported = new List<Metric>();
        using var metrics = new ParquetNeoMetrics();
        using (var provider = Sdk.CreateMeterProviderBuilder().AddParquetNeoInstrumentation().AddInMemoryExporter(exported).Build())
        {
            var storage = new InMemoryStorage { Options = new RangeReadStreamOptions { BlockSize = 1000, TailSize = 0, Metrics = metrics } }
                .Put("a", new byte[5000]);

            await using (var stream = await storage.OpenReadAsync("a"))
            {
                await stream.ReadExactlyAsync(new byte[10]);
            }

            provider.ForceFlush();
        }

        await Assert.That(exported.Select(metric => metric.Name)).Contains("parquetneo.storage.requests");
    }

    [Test]
    public async Task WithoutTheCall_NothingIsExported()
    {
        var exported = new List<System.Diagnostics.Activity>();
        using var tracing = new ParquetNeoTracing();
        using (Sdk.CreateTracerProviderBuilder().AddInMemoryExporter(exported).Build())
        {
            using var span = tracing.StartStorageOpen("x", "k");
        }

        await Assert.That(exported.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ABuilderThatIsNull_IsRefused()
    {
        await Assert.That(() => ParquetNeoTracerProviderBuilderExtensions.AddParquetNeoInstrumentation(null!)).Throws<ArgumentNullException>();
        await Assert.That(() => ParquetNeoMeterProviderBuilderExtensions.AddParquetNeoInstrumentation(null!)).Throws<ArgumentNullException>();
    }
}
