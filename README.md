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

#### Trimming and Native AOT

By default the serializer finds out how a type maps to columns by reflection and compiles that into code the first time the type
is used, which neither trimming nor Native AOT allows. Put `[ParquetSerializable]` on a type (or, for a type you do not own,
`[assembly: ParquetSerializable(typeof(TheirType))]`) and the source generator writes the mapping when you build, so nothing is
looked at or compiled at run time; the serializer is called as before. The generator handles properties that are single columns
(numbers, strings, dates, `Guid`, enums, byte arrays and their nullable forms), objects and structs (nested to any depth), and
arrays, lists, sets and dictionaries of any of them. A type created with a constructor without parameters is read with the
setters of its properties; one without, such as a record, is read by calling its constructor with the values of the properties it
names as parameters. For a type it cannot map (a property of a type that is not stored as columns, or no constructor that fits) it
reports a warning and the type keeps working through reflection; a type it can write but not read gets a warning of its own. In a
Native AOT program a type with no generated mapping is refused with an explanation.

#### Skipping row groups

Every row group records the smallest and largest value of each column, and how many are null. A filter checked against them skips the
row groups that cannot hold a row you want, which means they are not read (and with the asynchronous methods, not fetched):

```csharp
var recent = ParquetFilter.Column("Born").GreaterThanOrEqual(new DateOnly(1900, 1, 1)) & ParquetFilter.Column("Name").IsNotNull();
var rows = ParquetSerializer.Deserialize<Person>(stream, new ParquetDeserializerOptions { RowGroupFilter = recent });
```

A filter only prunes: the rows of the row groups that are read are all returned, so apply the condition to them as well when it
must hold exactly. `ParquetFileReader.GetMatchingRowGroups` and `ParquetDataReaderOptions.RowGroupFilter` take the same filter.

#### Checksums and bloom filters

Statistics rule out little when values are spread widely (ids, names). A bloom filter tells a reader that a value is certainly not
in a row group, so an equality filter skips it. Ask for one on the columns that are searched, and, if wanted, for the CRC-32 of every
page, which readers can verify:

```csharp
var written = new ParquetSerializerOptions
{
    Writer = new ParquetWriterOptions
    {
        WritePageChecksums = true,
        ColumnBloomFilters = new Dictionary<string, BloomFilterSettings> { ["Email"] = new() { FalsePositiveRate = 0.01 } },
    },
};
var found = ParquetSerializer.Deserialize<Person>(stream, new ParquetDeserializerOptions
{
    Reader = new ParquetReaderOptions { VerifyPageChecksums = true },       // a damaged page throws CorruptColumnException
    RowGroupFilter = ParquetFilter.Column("Email").Equal("ada@example.com"), // checked against the bloom filters too
});
```

Both are off by default because they cost time when writing: the checksums about a second per GB of page bytes, the bloom filters
(which hash and count every value of their columns) several times the time of writing those columns when the pages are not
compressed, and little when they are. Bloom filters in files written by other tools are read too (the layout follows the format specification); the asynchronous methods of
`ParquetSerializer` do not fetch them and check the statistics only.

#### Page index

A row group is made of many pages, and its statistics say nothing of which of them hold a value. A file written with a page index records where each page of a column starts and its smallest and largest value, so that a filter skips
pages as well as row groups, and only the pages that may hold a wanted row are read, decompressed and decoded:

```csharp
var options = new ParquetSerializerOptions { Writer = new ParquetWriterOptions { WritePageIndex = true } };
// ... Deserialize with a RowGroupFilter as above: the pages the filter rules out are not read either.
```

It pays when the values of a column are in some order (a time, an id that grows), which makes the bounds of a page tight. Reading 100
ids out of 2 million in one row group took 313 ms with the statistics of the row group only and 39 ms with the page index (158 and 48 ms
gzipped); the index took 2.5 KB of a 63 MB file, and writing took 8 to 10% longer, so it is off by default. A filter then returns the
rows of the pages it keeps, which still include rows it does not match. The low-level API has the same: `RowGroupReader.SelectRows(filter,
columns)` gives the rows to read, and `OpenColumn(index, selection)` reads only those. The asynchronous methods of `ParquetSerializer`
do not fetch the index.

#### LINQ over files

`ParquetNeo.Linq` queries a file, or all the files of a directory, with LINQ. A `Where` is turned into a filter that skips the files and
row groups whose statistics rule it out (the whole predicate is still applied to the rows that are read, so the result is exact), and a
query that ends in a `Select` reads only the columns it uses:

```csharp
using ParquetNeo.Linq;

var people = ParquetDataset.Open<Person>("people/");   // a file, or a directory (and the folders below it)
var names = people.Where(p => p.Born >= new DateOnly(1900, 1, 1) && p.Name != null)
                  .OrderBy(p => p.Born)
                  .Select(p => new { p.Name, p.Born })
                  .Take(100)
                  .ToList();

var plan = people.Where(p => p.Id > 5000).Explain();   // files and row groups skipped, columns read
int count = people.Count();                            // from the footers: no data is read
await foreach (var row in people.Where(p => p.Id > 5000).AsAsyncEnumerable()) { }
```

Every operator of LINQ to objects works on the rows that are read. Files are read in parallel, so without an `OrderBy` the rows
come in no particular order. The project builds expression trees and types at run time, so unlike the rest of the library it is not
safe with trimming or Native AOT.

### Low-level API

Describe the columns in a schema, then write one batch of values per column for each row group.

```csharp
using ParquetNeo;
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
| `src/ParquetNeo` | The core library: the file reader and writer, the schema, the encodings and codecs, and the ADO.NET reader and writer |
| `src/ParquetNeo.Serialization` | `ParquetSerializer`, which maps your classes and records to Parquet files, with the attributes that adjust it and the code the source generator writes against (add it to use the high-level API; it brings `ParquetNeo`) |
| `src/ParquetNeo.Linq` | `ParquetDataset<T>`: LINQ over a file or a directory of files, with skipping of files, row groups and columns (not trim or AOT safe) |
| `src/ParquetNeo.Generator` | The source generator behind `[ParquetSerializable]` (a Roslyn component, not a reference of your code) |
| `src/ParquetNeo.Enums` | The enumerations (the types of the Parquet format, and the options that name a choice), all in the `ParquetNeo` namespace |
| `tests/ParquetNeo.Tests` | Unit tests of `ParquetNeo` ([TUnit](https://tunit.dev/)) |
| `tests/ParquetNeo.Linq.Tests` | Unit tests of `ParquetNeo.Linq`: results against LINQ to objects, random predicates, plans, directories |
| `tests/ParquetNeo.Serialization.Tests` | Unit tests of `ParquetNeo.Serialization`, including the generated mappings against the reflection path |
| `tests/ParquetNeo.AotSmoke` | A program published with Native AOT in CI: it uses the serializer with a generated mapping |
| `tests/ParquetNeo.Enums.Tests`, `tests/ParquetNeo.Generator.Tests`, `tests/ParquetNeo.Compression.*.Tests` | Each project has a test project of its own; the add-on codec projects run the shared codec checks of `tests/ParquetNeo.Tests.Common` |
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
