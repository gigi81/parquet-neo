# ParquetNeo

A high-performance .NET library for reading and writing [Apache Parquet](https://parquet.apache.org/) files.

- **Fast:** span-based, allocation-conscious, SIMD where it pays off. See [Performance](PERFORMANCE.md).
- **Two layers:** a *low-level* API on row groups and column chunks, and a *high-level* API that reads and writes your classes and records.
- **Skips what it does not need:** filters rule out row groups and pages by their statistics, page index and bloom filters; queries read only the columns they use.
- **Trim and Native AOT safe:** a source generator writes the mapping of your types at build time.
- **Minimal dependencies:** the core depends only on the BCL; the codecs that need a library are separate packages.

```bash
dotnet add package ParquetNeo.All
```

```csharp
using ParquetNeo.Serialization;

using var file = File.Create("people.parquet");
ParquetSerializer.Serialize(people, file);

var back = ParquetSerializer.Deserialize<Person>(File.OpenRead("people.parquet"));

public record Person(int Id, string Name, DateOnly Born);
```

**Links:** [NuGet](https://www.nuget.org/packages/ParquetNeo.All) · [source on GitHub](https://github.com/gigi81/parquet-neo) · [releases](https://github.com/gigi81/parquet-neo/releases) · [report a problem](https://github.com/gigi81/parquet-neo/issues)

Start with [Getting started](articles/getting-started.md), or go to the [API reference](api/index.md).
