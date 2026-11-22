# Metrics

ParquetNeo records [metrics](https://learn.microsoft.com/dotnet/core/diagnostics/metrics) with `System.Diagnostics.Metrics`, which is part of .NET:
what it asks of a storage, how long that takes, how many row groups a filter skips, and how long the queries of a dataset take. Any
collector of .NET metrics can read them: OpenTelemetry (and so Prometheus, Azure Monitor, Grafana, ...), `dotnet-counters`, or your own `MeterListener`.
Nothing is exported by the library, and nothing is recorded unless you give it a `ParquetNeoMetrics`.

The same events are also traced, as spans: see [Tracing](tracing.md). `ParquetNeo.OpenTelemetry` has `AddParquetNeoInstrumentation()` for the tracer provider
and the meter provider of OpenTelemetry, which subscribes to both with one call.

## With dependency injection

Add the package `ParquetNeo.Extensions.DependencyInjection` (it is in `ParquetNeo.All`) and register the metrics. Their meter comes from the
`IMeterFactory` of the container, as the meters of ASP.NET Core do:

```csharp
using ParquetNeo.Diagnostics;

builder.Services.AddParquetNeoMetrics();     // an extension of IServiceCollection, in the namespace Microsoft.Extensions.DependencyInjection, which a host already uses

builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics => metrics
        .AddMeter(TelemetryNames.Name)      // "ParquetNeo"; or .AddParquetNeoInstrumentation() of the package ParquetNeo.OpenTelemetry
        .AddPrometheusExporter());                  // or any other exporter
```

Then hand the metrics to what you want measured. They go in the options, which is where the code that records them takes them from, so there is
no global state, and two parts of an application can have separate metrics:

```csharp
builder.Services.AddSingleton<IParquetStorage>(provider =>
{
    var metrics = provider.GetRequiredService<ParquetNeoMetrics>();
    return new S3ParquetStorage(new AmazonS3Client(), "my-bucket", new RangeReadStreamOptions { Metrics = metrics });
});

builder.Services.AddSingleton(provider => new ParquetDatasetOptions { Metrics = provider.GetRequiredService<ParquetNeoMetrics>() });
```

## Without it

```csharp
using var metrics = new ParquetNeoMetrics();       // makes its own meter, which is disposed with it

var reader = new ParquetDeserializerOptions { Reader = new ParquetReaderOptions { Metrics = metrics } };
var rows = ParquetSerializer.Deserialize<Person>(stream, reader);
```

Collect them with, for example, `dotnet-counters monitor --counters ParquetNeo --process-id <pid>`.

## Where to give them

| To measure | Give the metrics to |
|---|---|
| Requests to a storage, and the objects and listings | `RangeReadStreamOptions.Metrics`, the last argument of `S3ParquetStorage` and `AzureBlobParquetStorage` |
| Row groups read and skipped, rows the page index skips | `ParquetReaderOptions.Metrics` (of `ParquetFileReader`, `ParquetDeserializerOptions.Reader`, `ParquetDataReaderOptions.Reader`) |
| Queries of a dataset | `ParquetDatasetOptions.Metrics`, which also gives them to the readers of the files of the dataset |

## The instruments

| Name | Kind (unit) | Tags | What it counts |
|---|---|---|---|
| `parquetneo.storage.requests` | counter (`{request}`) | `parquetneo.storage.system`, `parquetneo.outcome` | requests for a range of an object |
| `parquetneo.storage.request.duration` | histogram (`s`) | the same | how long a request took |
| `parquetneo.storage.bytes_read` | counter (`By`) | `parquetneo.storage.system` | bytes received |
| `parquetneo.storage.objects_opened` | counter (`{object}`) | `parquetneo.storage.system` | objects opened for reading |
| `parquetneo.storage.listings` | counter (`{listing}`) | `parquetneo.storage.system` | listings of a prefix |
| `parquetneo.row_groups.read` | counter (`{row_group}`) | | row groups that are going to be read |
| `parquetneo.row_groups.skipped` | counter (`{row_group}`) | | row groups a filter ruled out (statistics or bloom filters) |
| `parquetneo.rows.skipped` | counter (`{row}`) | | rows of the row groups that are read that the page index ruled out |
| `parquetneo.query.duration` | histogram (`s`) | `parquetneo.query.filtered` | a query of a dataset, from the first row asked for to the last one given |
| `parquetneo.query.rows` | counter (`{row}`) | `parquetneo.query.filtered` | rows the queries of a dataset read from the files |

The values of `parquetneo.storage.system` are `aws_s3` and `azure_blob`; a storage of your own (a `RangeReadStream`) says its own by overriding
`StorageSystem`, and `other` is the default. `parquetneo.outcome` is `ok` or `error`; a failed request counts as a request, takes part in the duration,
and has no bytes. `parquetneo.query.filtered` is whether the query had a condition that could skip files and row groups.

A query that takes only the first rows (`Take`, `First`) is recorded when its enumeration ends, with the rows it read until then, and
`Count()` and `Any()` answered from the footers are queries that read no rows.

## Metrics and `ReadStatistics`

[`ReadStatistics`](object-storage.md#how-much-was-read) is a counter you read in code, per dataset or per `MeteredStorage`: useful in a test or a
tool to see what one operation cost. The metrics are for monitoring an application: the same events, over time and for the whole process, with the
tags and the durations. They can be used together.

## Cost

An instrument that nobody listens to does almost nothing when it is recorded, and code that has no metrics given to it checks for that and does
no work at all. Recording needs no allocation. The metrics are safe to use from several threads and with Native AOT.
