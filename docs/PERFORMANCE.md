# Performance

How ParquetNeo compares with [Parquet.Net](https://github.com/aloneguid/parquet-dotnet) (6.1.0) and
[ParquetSharp](https://github.com/G-Research/ParquetSharp) (25.0.1.2, a wrapper of the Apache Arrow C++ implementation), and
what was done about what the measurements showed.

## What is measured

One dataset of 1,000,000 rows and five columns: a required `long`, a nullable string with 5,000 distinct values (one null in
ten), a nullable `double` (one null in three), a required `int` in a small range and a required `bool`. It is written as one
row group, with the default encodings of each library (dictionary encoding where the library chooses it), uncompressed and
with Snappy, to and from memory.

- **columns**: the column-wise API of each library, which is the closest to the file (`ParquetFileWriter` for ParquetNeo,
  `ParquetWriter` for Parquet.Net, `ParquetFileWriter` with `LogicalWriter` for ParquetSharp).
- **serializer**: objects in, objects out (`ParquetSerializer` of ParquetNeo and of Parquet.Net). ParquetSharp has none.
- Reading: every library reads the same file, written by ParquetSharp, into arrays (columns) or into objects (serializer).

```bash
dotnet run -c Release --project benchmarks/ParquetNeo.Benchmarks -- --filter "*WriteComparison*"
```

```bash
dotnet run -c Release --project benchmarks/ParquetNeo.Benchmarks -- --filter "*ReadComparison*"
```

`ColumnBreakdown` times each column of the low-level writer and reader on its own, which is how the work below was found.

## Results

BenchmarkDotNet 0.15.8, .NET 10.0.14, Intel Xeon 2.10 GHz, 4 cores, Linux. Mean of 6 iterations after 2 warm-up iterations (a
short job, so the error margins are wide: see the notes). Smaller is better.

### Writing

| | Uncompressed | Snappy | Allocated |
|---|---:|---:|---:|
| ParquetNeo, columns | 81 ms | 97 ms | 52 MB |
| Parquet.Net, columns | 84 ms | 108 ms | 28 MB |
| ParquetSharp, columns | 108 ms | 128 ms | 13 MB |
| ParquetNeo, serializer | 143 ms | 162 ms | 87 MB |
| ParquetNeo, serializer, 4 threads | 128 ms | 121 ms | 120–146 MB |
| Parquet.Net, serializer | 142 ms | 158 ms | 125 MB |

### Reading

| | Uncompressed | Snappy | Allocated |
|---|---:|---:|---:|
| ParquetNeo, columns | 22 ms | 43 ms | 34 MB |
| Parquet.Net, columns | 94 ms | 93 ms | 77–96 MB |
| ParquetSharp, columns | 62 ms | 87 ms | 36 MB |
| ParquetNeo, serializer | 125 ms | 157 ms | 106 MB |
| Parquet.Net, serializer | 176 ms | 172 ms | 148–167 MB |

### Notes on the numbers

- They come from one machine, one dataset and a short BenchmarkDotNet job. Differences of 10 to 20 percent between two rows
  are within the noise (the error column of the BenchmarkDotNet output is up to about 30 ms for writes, and the
  Parquet.Net serializer read has an error larger than its mean). The large differences, such as reading through columns, are not.
- Writing through columns is on a par with Parquet.Net and ahead of ParquetSharp. The serializer takes about 1.7 times as long
  as the column API: it is on a par with Parquet.Net's when writing, and ahead of it when reading.
- ParquetSharp, being native code behind a managed interface, allocates the least; ParquetNeo allocates more than both when
  writing through columns. Encoding columns on several threads holds the encoded columns of a row group at the same time, which
  is where its extra allocation comes from.

## What the measurements led to

Timing each column alone (`ColumnBreakdown`) showed that one column dominated both directions: the dictionary encoded string
column took 93% of the time of the reader and 60% of the writer. Hence:

- **Strings of a dictionary are decoded once.** Reading text turned every value into a new string, 900,000 of them for 5,000
  distinct values. A text dictionary now turns each entry into a string the first time it is used and hands out that string
  for every row that refers to it. Reading the string column went from 77 ms and 42 MB to 8 ms and 0.25 MB, and the whole
  table through columns from 108 ms to 23 ms. A batch of text also no longer ends at the end of a page, since the values are
  copies and no longer point into the page.
- **Strings are encoded in one pass.** Up to 1,024 characters, a string is encoded to UTF-8 straight into a buffer sized for
  the longest it can be, instead of being measured and then encoded. Plain encoded string columns write about 20 percent
  faster.
- **Statistics of a dictionary column look at new entries only.** The minimum and maximum of a column cannot change when a value
  already in the dictionary comes again, so only values new to the dictionary are compared (900,000 comparisons became 5,000
  here).

None of this changes the files that are written.

A second round went to the serializer, whose profile showed the encoders at about 60% of a write and the shredding of rows at
about a third:

- **Columns can be encoded in parallel** (`MaxDegreeOfParallelism` of `ParquetSerializerOptions` and of
  `ParquetDataWriterOptions`; 1, the calling thread, by default). Each column of a row group is encoded into a buffer of its own
  on a different thread and the buffers are copied to the file in the order of the schema, so the file is the same, byte for
  byte, whatever the degree. With four threads the serializer writes the table in 128 ms (121 ms with Snappy) instead of 143 ms
  (162 ms); the time is bounded by the slowest column, here the strings.
- **The column buffers are made big enough up front** (the size of the row group, or the number of rows when the collection says
  how many there are) and no longer grow by doubling and copying.
- **The synchronous serializer writes straight to the stream** and no longer stages the file in memory to copy it.
- **A property is read with one delegate call** instead of two, and a reference is stored in a buffer without the check an
  array of references makes.
- **Reading rows sizes its lists and arrays from the file**: the list of rows (which was also copied once more into the list that
  is returned) and the arrays that hold a column. The numbers are those of the file, so they are only trusted up to two million
  entries and the arrays grow normally beyond that.

The serializer writes in 143 ms where it took 198 ms, with 87 MB allocated instead of 188 MB, and reads in 125 ms where it took 186 ms,
with 106 MB allocated instead of 162 MB.

## Not done yet

Left for a follow-up, with what is known so far:

- **The rows themselves.** What is left of the serializer's time is around the data: shredding each row into the column buffers
  (virtual calls and delegates per value) when writing, and creating the objects and setting their properties when reading. The
  next step would be to compile one function per row type that does it all, instead of walking a tree of small objects.
- **Parallel encoding for the column API.** `RowGroupWriter` can encode all the columns of a group in parallel, but only the
  serializer and the ADO.NET writer use it; there is no public form for people who write columns by hand.
- **Encoding strings.** The dictionary encoded string column is still the slowest column to write (about 47 ms of the 81 ms
  here), mostly in hashing and looking up values and in the loop around them.
- **Allocation of the column writer** is higher than that of the other libraries (the pages are held before they are written).
- **Giving up a dictionary early.** A column of unique values (an identifier) fills a dictionary up to its size limit before the
  writer gives it up. Deciding sooner would be faster, but changes which files are written for such columns.
- **Asynchronous input and output** has not been tuned, and the asynchronous serializer still stages the file in memory.
