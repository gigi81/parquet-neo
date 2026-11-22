using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using ParquetNeo.Diagnostics;

namespace OpenTelemetry.Trace
{
    /// <summary>Adds the traces of ParquetNeo to OpenTelemetry.</summary>
    public static class ParquetNeoTracerProviderBuilderExtensions
    {
        /// <summary>
        /// Subscribes the tracer provider to the spans of ParquetNeo (the source <see cref="TelemetryNames.Name"/>): the requests to a storage, the
        /// objects it opens and lists, the row groups that are read and the queries of a dataset. What is traced is what is given a
        /// <see cref="ParquetNeoTracing"/> in its options.
        /// </summary>
        public static TracerProviderBuilder AddParquetNeoInstrumentation(this TracerProviderBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            return builder.AddSource(TelemetryNames.Name);
        }
    }
}

namespace OpenTelemetry.Metrics
{
    /// <summary>Adds the metrics of ParquetNeo to OpenTelemetry.</summary>
    public static class ParquetNeoMeterProviderBuilderExtensions
    {
        /// <summary>
        /// Subscribes the meter provider to the metrics of ParquetNeo (the meter <see cref="TelemetryNames.Name"/>): the requests to a storage and
        /// what they read, the row groups that are read and skipped, and the queries of a dataset. What is measured is what is given a
        /// <see cref="ParquetNeoMetrics"/> in its options.
        /// </summary>
        public static MeterProviderBuilder AddParquetNeoInstrumentation(this MeterProviderBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            return builder.AddMeter(TelemetryNames.Name);
        }
    }
}
