# Low-level API

The low-level API (package `ParquetNeo`) works on row groups and column chunks, with `Span<T>` and definition and repetition
levels. The serializer is built on it.

## Writing

Describe the columns in a schema, then write one batch of values per column for each row group:

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

With several columns, `WriteColumns` encodes them on different threads and makes the same file (it holds the encoded group in memory
until all columns are done):

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

The footer is written when the writer is disposed.

## Reading

```csharp
using var reader = new ParquetFileReader(stream);
var group = reader.OpenRowGroup(0);
using var column = group.OpenColumn("id");

var values = new long[1000];
BatchRead read;
while ((read = column.ReadBatch(values)).Entries > 0)
{
    // use values[..read.Entries]
}
```

An optional column also takes a span for its definition levels: it receives `0` for a null and the maximum level for a value, and
`values` receives only the values that are not null, packed at the start. A column in a list also takes the repetition levels.

Memory is bounded by the largest page: pages are read one at a time and their buffers come from a pool.

## Statistics and metadata

`RowGroupReader.GetStatistics` gives the smallest and largest value and the null count of a column, as .NET values, and
`ParquetFileReader.Metadata` is the footer: the schema, the row groups and their column chunks.

## Safety limits

`ParquetReaderOptions` has limits that bound what a corrupt or hostile file can make the reader allocate (for example `MaxPageSize`). A damaged file throws a
`ParquetException` that says what is wrong.
