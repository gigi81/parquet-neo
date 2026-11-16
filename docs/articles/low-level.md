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

### Without blocking a thread

Every operation that touches the stream has an asynchronous form, so that a slow stream (a network, an object store upload) does not
hold a thread. `ParquetFileWriter.CreateAsync` starts the file, `RowGroupWriter.WriteColumnsAsync` (all the columns, encoded in
parallel on the thread pool) or `WriteColumnAsync` (one column at a time, which holds only one encoded column in memory) write the
columns, and `CloseAsync` or `DisposeAsync` finish the group and the file, including the bloom filters, the page indexes and the
footer. The files are the same, byte for byte, as the ones the synchronous methods make:

```csharp
await using var writer = await ParquetFileWriter.CreateAsync(stream, schema);
await using (var group = writer.AppendRowGroup())
{
    await group.WriteColumnAsync(column => column.WriteBatch([1L, 2L, 3L]));
    await group.WriteColumnAsync(column => column.WriteStrings(["Ada", "Alan", "Grace"]));
}
```

`NextColumn` is the synchronous way: it encodes and writes each page as it goes, so it blocks on the stream. The encoding itself is
CPU work and runs on the calling thread (or, for `WriteColumnsAsync`, on pool threads).

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

### Without blocking a thread

Reading works from the stream synchronously, one page at a time. The asynchronous forms read what is needed first, with
asynchronous reads, and then decode it from memory: `ParquetFileReader.OpenAsync` reads the footer, `RowGroupReader.OpenColumnAsync`
reads the whole column chunk (or, given a `RowSelection`, its page index and the pages that hold the selected rows), and
`GetMatchingRowGroupsAsync` and `SelectRowsAsync` read the bloom filters and page indexes they need. The chunk stays in memory until
the `ColumnReader` is disposed, so the memory is the compressed size of the column chunk: use the synchronous methods for a chunk too
big to hold.

```csharp
await using var reader = await ParquetFileReader.OpenAsync(stream);
using var column = await reader.OpenRowGroup(0).OpenColumnAsync("id");
var values = new long[1000];
var read = column.ReadBatch(values); // no wait for the stream: the chunk is in memory
```

`ParquetDataReader.OpenAsync` and `ReadAsync` (ADO.NET) do the same for each row group.
