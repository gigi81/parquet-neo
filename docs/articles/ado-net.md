# ADO.NET

Write the rows of any `IDataReader` (a database query, `DataTable.CreateDataReader()`, ...) as a Parquet file, and read a Parquet
file as a `DbDataReader`, which works with `SqlBulkCopy`, `DataTable.Load` and the like. This is in the `ParquetNeo` package.

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

The writer takes the type of each column from `GetFieldType`, and the nullability and the precision of decimals from the schema
table of the reader. It writes row groups of a set size, so memory does not depend on the number of rows. `WriteAsync` is the
asynchronous version.

`ParquetDataReaderOptions` chooses which columns to read, and takes a filter that skips row groups and pages that cannot hold the
rows you want: see [Skipping what is not needed](filtering.md).
