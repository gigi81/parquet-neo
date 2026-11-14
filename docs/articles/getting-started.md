# Getting started

## Install

```bash
dotnet add package ParquetNeo.All
```

This installs everything. To take less, see [Packages](packages.md).

## Write and read rows

Each public property of a class or record becomes a column. Nested objects, lists and dictionaries work too.

```csharp
using ParquetNeo.Serialization;

var people = new List<Person>
{
    new(1, "Ada", new DateOnly(1815, 12, 10)),
    new(2, "Alan", new DateOnly(1912, 6, 23)),
    new(3, "Grace", new DateOnly(1906, 12, 9)),
};

using (var file = File.Create("people.parquet"))
{
    ParquetSerializer.Serialize(people, file);
}

using var input = File.OpenRead("people.parquet");
List<Person> back = ParquetSerializer.Deserialize<Person>(input);

public record Person(int Id, string Name, DateOnly Born);
```

There are asynchronous versions of both (`SerializeAsync`, `DeserializeAsync`, and `DeserializeAllAsync` for an
`IAsyncEnumerable<T>`), and `DeserializeAll` reads one row group at a time so that a big file does not have to fit in memory.

## Where to go next

| You want to | Read |
|---|---|
| Control how classes map to columns, row group size, nulls | [Serializing your classes](serializer.md) |
| Publish with Native AOT or trimming | [Trimming and Native AOT](aot.md) |
| Smaller files | [Compression](compression.md) |
| Read only part of a big file | [Skipping what is not needed](filtering.md) and [LINQ over files](linq.md) |
| Work on columns and spans, without objects | [Low-level API](low-level.md) |
| Move rows between Parquet and a database | [ADO.NET](ado-net.md) |
