using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using ParquetNeo.Diagnostics;
using ParquetNeo.Storage;
using ParquetNeo.Tests.Diagnostics;
using ParquetNeo.Tests.Storage;

namespace ParquetNeo.Extensions.DependencyInjection.Tests;

/// <summary>Registering the metrics of ParquetNeo with a container.</summary>
public class ParquetNeoMetricsRegistrationTests
{
    [Test]
    public async Task TheMetrics_AreOneInstanceOfTheContainer()
    {
        using var provider = new ServiceCollection().AddParquetNeoMetrics().BuildServiceProvider();

        var first = provider.GetRequiredService<ParquetNeoMetrics>();
        var second = provider.GetRequiredService<ParquetNeoMetrics>();

        await Assert.That(first).IsSameReferenceAs(second);
    }

    [Test]
    public async Task TheMeter_ComesFromTheMeterFactoryOfTheContainer()
    {
        var services = new ServiceCollection();
        var factory = new RecordingMeterFactory();
        services.AddSingleton<IMeterFactory>(factory);
        services.AddParquetNeoMetrics();
        using var provider = services.BuildServiceProvider();

        var metrics = provider.GetRequiredService<ParquetNeoMetrics>();

        await Assert.That(factory.Names).IsEquivalentTo(new[] { ParquetNeoMetrics.MeterName });
        await Assert.That(metrics.Meter.Name).IsEqualTo(ParquetNeoMetrics.MeterName);
    }

    [Test]
    public async Task WithoutAMeterFactory_TheMetricsAreStillAvailable_AndRecordForListeners()
    {
        using var provider = new ServiceCollection().AddParquetNeoMetrics().BuildServiceProvider();
        var metrics = provider.GetRequiredService<ParquetNeoMetrics>();
        using var recorder = new MetricRecorder(metrics.Meter);
        var storage = new InMemoryStorage { Options = new RangeReadStreamOptions { TailSize = 0, Metrics = metrics } }.Put("a", new byte[500]);

        await using (var stream = await storage.OpenReadAsync("a"))
        {
            await stream.ReadExactlyAsync(new byte[20]);
        }

        await Assert.That(recorder.Sum("parquetneo.storage.requests")).IsEqualTo(1);
        await Assert.That(recorder.Sum("parquetneo.storage.bytes_read")).IsEqualTo(500);
    }

    [Test]
    public async Task AnApplicationThatRegisteredItsOwnMetrics_KeepsThem()
    {
        var services = new ServiceCollection();
        var own = new ParquetNeoMetrics();
        services.AddSingleton(own);
        services.AddParquetNeoMetrics();
        using var provider = services.BuildServiceProvider();

        await Assert.That(provider.GetRequiredService<ParquetNeoMetrics>()).IsSameReferenceAs(own);
        own.Dispose();
    }

    [Test]
    public async Task AddingThemTwice_IsHarmless_AndNullIsRefused()
    {
        var services = new ServiceCollection().AddParquetNeoMetrics().AddParquetNeoMetrics();
        using var provider = services.BuildServiceProvider();

        await Assert.That(services.Count(d => d.ServiceType == typeof(ParquetNeoMetrics))).IsEqualTo(1);
        await Assert.That(provider.GetRequiredService<ParquetNeoMetrics>()).IsNotNull();
        await Assert.That(() => ParquetNeoServiceCollectionExtensions.AddParquetNeoMetrics(null!)).Throws<ArgumentNullException>();
    }

    private sealed class RecordingMeterFactory : IMeterFactory
    {
        public List<string> Names { get; } = [];

        public Meter Create(MeterOptions options)
        {
            Names.Add(options.Name);
            return new Meter(options);
        }

        public void Dispose()
        {
        }
    }
}
