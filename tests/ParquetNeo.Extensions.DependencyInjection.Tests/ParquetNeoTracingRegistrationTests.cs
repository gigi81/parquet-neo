using Microsoft.Extensions.DependencyInjection;
using ParquetNeo.Diagnostics;
using ParquetNeo.Storage;
using ParquetNeo.Tests.Diagnostics;
using ParquetNeo.Tests.Storage;

namespace ParquetNeo.Extensions.DependencyInjection.Tests;

/// <summary>Registering the traces of ParquetNeo with a container.</summary>
public class ParquetNeoTracingRegistrationTests
{
    [Test]
    public async Task TheTracing_IsOneInstanceOfTheContainer_WhoseSourceIsNamedParquetNeo()
    {
        using var provider = new ServiceCollection().AddParquetNeoTracing().BuildServiceProvider();

        var first = provider.GetRequiredService<ParquetNeoTracing>();
        var second = provider.GetRequiredService<ParquetNeoTracing>();

        await Assert.That(first).IsSameReferenceAs(second);
        await Assert.That(first.Source.Name).IsEqualTo(ParquetNeoTracing.SourceName);
    }

    [Test]
    public async Task AnInstanceThatTheApplicationRegistered_IsKept()
    {
        using var own = new ParquetNeoTracing();
        var services = new ServiceCollection();
        services.AddSingleton(own);
        services.AddParquetNeoTracing();
        using var provider = services.BuildServiceProvider();

        await Assert.That(provider.GetRequiredService<ParquetNeoTracing>()).IsSameReferenceAs(own);
    }

    [Test]
    public async Task TheTracingOfTheContainer_RecordsForListeners_AndIsDisposedWithTheContainer()
    {
        var provider = new ServiceCollection().AddParquetNeoTracing().BuildServiceProvider();
        var tracing = provider.GetRequiredService<ParquetNeoTracing>();
        using var recorder = new TraceRecorder(tracing.Source);
        var storage = new InMemoryStorage { Options = new RangeReadStreamOptions { BlockSize = 1000, TailSize = 0, Tracing = tracing } }.Put("a", new byte[3000]);

        await using (var stream = await storage.OpenReadAsync("a"))
        {
            await stream.ReadExactlyAsync(new byte[10]);
        }

        await Assert.That(recorder.Of(ParquetNeoTracing.StorageRequestSpan).Count).IsEqualTo(1);
        provider.Dispose();
        using var afterwards = tracing.StartStorageOpen("x", "k");
        await Assert.That(recorder.Of(ParquetNeoTracing.StorageOpenSpan).Count).IsEqualTo(0);
    }

    [Test]
    public async Task AddingTheTracing_ToANullCollection_IsRefused()
    {
        await Assert.That(() => ParquetNeoServiceCollectionExtensions.AddParquetNeoTracing(null!)).Throws<ArgumentNullException>();
    }
}
