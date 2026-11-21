# Tracing

ParquetNeo records [traces](https://learn.microsoft.com/dotnet/core/diagnostics/distributed-tracing) with `System.Diagnostics.Activity`, which is part of
.NET: a span for each request to a storage, for each object opened and listed, for each row group that is read, and for each query of a dataset. They
complement the [metrics](metrics.md): the metrics say how much, over time; a trace says what one request did, and which request caused which. Any
collector of .NET traces can read them: OpenTelemetry (and so Jaeger, Zipkin, Azure Monitor, Grafana Tempo, ...), or your own `ActivityListener`.
Nothing is exported by the library, and nothing is traced unless you give it a `ParquetNeoTracing`.

## With OpenTelemetry

Add the package `ParquetNeo.OpenTelemetry` (it is in `ParquetNeo.All`), and the package `ParquetNeo.Extensions.DependencyInjection` for the registration:

```csharp
using ParquetNeo.Diagnostics;

builder.Services.AddParquetNeoMetrics();
builder.Services.AddParquetNeoTracing();     // an extension of IServiceCollection, in the namespace Microsoft.Extensions.DependencyInjection

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddParquetNeoInstrumentation()      // the source "ParquetNeo"; an extension in the namespace OpenTelemetry.Trace
        .AddOtlpExporter())
    .WithMetrics(metrics => metrics
        .AddParquetNeoInstrumentation()      // the meter "ParquetNeo"; the same name for the metrics, in the namespace OpenTelemetry.Metrics
        .AddOtlpExporter());
```

`AddParquetNeoInstrumentation()` only subscribes to the source or the meter: it is what `AddSource(ParquetNeoTracing.SourceName)` and
`AddMeter(ParquetNeoMetrics.MeterName)` do. Without the package, those two calls do the same.

Then hand the tracing to what you want traced. Like the metrics, it goes in the options, where the code that records it takes it from, so there is no
global state:

```csharp
builder.Services.AddSingleton<IParquetStorage>(provider =>
{
    var options = new RangeReadStreamOptions
    {
        Metrics = provider.GetRequiredService<ParquetNeoMetrics>(),
        Tracing = provider.GetRequiredService<ParquetNeoTracing>(),
    };
    return new S3ParquetStorage(new AmazonS3Client(), "my-bucket", options);
});

builder.Services.AddSingleton(provider => new ParquetDatasetOptions
{
    Metrics = provider.GetRequiredService<ParquetNeoMetrics>(),
    Tracing = provider.GetRequiredService<ParquetNeoTracing>(),
});
```

## Without dependency injection

```csharp
using var tracing = new ParquetNeoTracing();       // makes its own ActivitySource, which is disposed with it

using var provider = Sdk.CreateTracerProviderBuilder()
    .AddSource(ParquetNeoTracing.SourceName)       // or AddParquetNeoInstrumentation()
    .AddConsoleExporter()
    .Build();

var reader = new ParquetDeserializerOptions { Reader = new ParquetReaderOptions { Tracing = tracing } };
var rows = ParquetSerializer.Deserialize<Person>(stream, reader);
```

## Where to give it

| To trace | Give the tracing to |
|---|---|
| Requests to a storage, and the objects it opens and lists | `RangeReadStreamOptions.Tracing`, the last argument of `S3ParquetStorage` and `AzureBlobParquetStorage` |
| The row groups that the serializer reads | `ParquetReaderOptions.Tracing` (of `ParquetDeserializerOptions.Reader`) |
| Queries of a dataset | `ParquetDatasetOptions.Tracing`, which also gives it to the readers of the files of the dataset |

## The spans

| Name | Kind | Tags | What it is |
|---|---|---|---|
| `parquetneo.storage.request` | client | `parquetneo.storage.system`, `parquetneo.storage.offset`, `parquetneo.storage.bytes` | a request for a range of an object; the bytes are those asked for, and those received when it ends |
| `parquetneo.storage.open` | client | `parquetneo.storage.system`, `parquetneo.storage.key` | the opening of an object for reading (its size and version are asked of the storage) |
| `parquetneo.storage.list` | client | `parquetneo.storage.system`, `parquetneo.storage.key` | a request of a listing of a prefix (the tag `parquetneo.storage.key` is the prefix); a listing of several pages has a span for each |
| `parquetneo.row_group.read` | internal | `parquetneo.row_group.index`, `parquetneo.row_group.rows` | the reading of the columns of a row group and the building of its rows |
| `parquetneo.query` | internal | `parquetneo.query.filtered`, `parquetneo.query.rows` | a query of a dataset, from the first row asked for to the last one given |

The tags of a storage and of a query are those of the [metrics](metrics.md#the-instruments). A span that ends with an exception has the status `Error`, the
message as its description, and the tag `error.type` with the type of the exception; the exception is thrown again as it was. The key of an object, which
can be private, is a tag of the spans of the storages: leave the tracing out of the options of a storage, or drop the tag in your exporter, if it must not leave
the application.

## Which span is the parent

A span is the child of the span that is current where the work is done: your own span holds the query, and a query holds the row groups it reads and the
requests that they cause (the readers of the files take the current span with them, whether they are PLINQ workers or the tasks of an asynchronous query).
The span of a query is current only while a row is being asked for: between two rows, your code goes on with its own span, and the query is not the parent of
what you do with the rows. A query that is not enumerated to its end, such as `Take` or `First`, ends when its enumeration is disposed.

## Cost

Code that has no tracing given to it does no work for it. With a `ParquetNeoTracing` and no listener (nobody subscribed to the source), starting a span
returns `null` and allocates nothing. The tracing is safe to use from several threads and with Native AOT.

## What is not traced

Writing is not traced: nothing in `ParquetFileWriter` records spans or metrics yet. Opening a file (its footer) is not a span of its own, but the requests that
read it are, below the span that is current.
