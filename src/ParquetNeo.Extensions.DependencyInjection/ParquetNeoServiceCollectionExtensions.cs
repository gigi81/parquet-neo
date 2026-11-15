using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ParquetNeo.Diagnostics;

namespace ParquetNeo.Extensions.DependencyInjection;

/// <summary>Registers ParquetNeo with a service collection.</summary>
public static class ParquetNeoServiceCollectionExtensions
{
    /// <summary>
    /// Adds the <see cref="ParquetNeoMetrics"/> of the application as a singleton, whose meter is made by the <see cref="IMeterFactory"/> of the
    /// container (which this method adds, as <c>AddMetrics()</c> does, when the application has not). Give it to what you want measured:
    /// <code>
    /// var metrics = services.GetRequiredService&lt;ParquetNeoMetrics&gt;();
    /// var reader = new ParquetReaderOptions { Metrics = metrics };
    /// </code>
    /// To collect the measurements, subscribe to the meter named <see cref="ParquetNeoMetrics.MeterName"/> (in OpenTelemetry,
    /// <c>metrics.AddMeter(ParquetNeoMetrics.MeterName)</c>).
    /// </summary>
    public static IServiceCollection AddParquetNeoMetrics(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddMetrics();
        services.TryAddSingleton(provider => new ParquetNeoMetrics(provider.GetRequiredService<IMeterFactory>()));
        return services;
    }
}
