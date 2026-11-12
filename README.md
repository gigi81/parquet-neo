# ParquetNeo

A high-performance .NET library for reading and writing [Apache Parquet](https://parquet.apache.org/) files.

> **Status:** early development. The low-level reader and writer and the high-level serializer work, but the API may still change and there is no NuGet package yet — see the [implementation plan](docs/PLAN.md).

## Goals

- **Fast:** span-based, allocation-conscious, SIMD where it pays off.
- **Two layers:**
  - a *low-level* API working on row groups and column chunks,
  - a *high-level* API that reads and writes plain DTOs.
- **Minimal dependencies:** the core depends only on the BCL.

## Writing a file

### High-level API

Each public property of a class or record becomes a column. Nested objects, lists and dictionaries work too.

```csharp
using ParquetNeo.Serialization;

var people = new List<Person>
{
    new(1, "Ada", new DateOnly(1815, 12, 10)),
    new(2, "Alan", new DateOnly(1912, 6, 23)),
    new(3, "Grace", new DateOnly(1906, 12, 9)),
};

using var file = File.Create("people.parquet");
ParquetSerializer.Serialize(people, file);

public record Person(int Id, string Name, DateOnly Born);
```

Read the rows back with `ParquetSerializer.Deserialize<Person>(stream)`.

### Low-level API

Describe the columns in a schema, then write one batch of values per column for each row group.

```csharp
using ParquetNeo;
using ParquetNeo.Core;
using ParquetNeo.Metadata;
using ParquetNeo.Schema;

var schema = new ParquetSchema(
[
    new PrimitiveNode("id", PhysicalType.Int64, FieldRepetitionType.Required),
    new PrimitiveNode("name", PhysicalType.ByteArray, FieldRepetitionType.Required, LogicalType.String),
]);

using var file = File.Create("people.parquet");
using var writer = new ParquetFileWriter(file, schema);
using (var group = writer.AppendRowGroup())
{
    group.NextColumn().WriteBatch([1L, 2L, 3L]);
    group.NextColumn().WriteStrings(["Ada", "Alan", "Grace"]);
}
```

With several columns, `WriteColumns` encodes them on different threads and makes the same file (it holds the encoded group in memory until all columns are done):

```csharp
using (var group = writer.AppendRowGroup())
{
    group.WriteColumns(
    [
        column => column.WriteBatch([1L, 2L, 3L]),
        column => column.WriteStrings(["Ada", "Alan", "Grace"]),
    ]);
}
```

The footer is written when the writer is disposed. Read a column back with
`new ParquetFileReader(stream).OpenRowGroup(0).OpenColumn("id").ReadBatch(values)`.

## ADO.NET

Write the rows of any `IDataReader` (a database query, `DataTable.CreateDataReader()`, ...) as a Parquet file, and read a Parquet file as a `DbDataReader`, which works with `SqlBulkCopy`, `DataTable.Load` and the like.

```csharp
using ParquetNeo.Data;

using var query = command.ExecuteReader();
using var output = File.Create("orders.parquet");
ParquetDataWriter.Write(query, output);

using var input = File.OpenRead("orders.parquet");
using var reader = new ParquetDataReader(input);
while (reader.Read())
{
    Console.WriteLine($"{reader.GetInt64(0)} {reader.GetString(1)}");
}
```

## Repository layout

| Path | Contents |
|---|---|
| `src/ParquetNeo` | The library |
| `src/ParquetNeo.Enums` | The enumerations (the types of the Parquet format, and the options that name a choice), all in the `ParquetNeo` namespace |
| `tests/ParquetNeo.Tests` | Unit tests of `ParquetNeo` ([TUnit](https://tunit.dev/)) |
| `tests/ParquetNeo.Enums.Tests`, `tests/ParquetNeo.Compression.*.Tests` | Each project has a test project of its own; the add-on codec projects run the shared codec checks of `tests/ParquetNeo.Tests.Common` |
| `benchmarks/ParquetNeo.Benchmarks` | [BenchmarkDotNet](https://benchmarkdotnet.org/) benchmarks |
| `specs` | Local copy of the Parquet format specification and `parquet.thrift` |
| `docs` | Project documentation, including the plan |

## Building and testing

Requires the .NET 10 SDK.

```bash
dotnet build -c Release
```

```bash
dotnet test -c Release
```

## License

MIT — see [LICENSE.md](LICENSE.md).
