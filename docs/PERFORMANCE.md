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
| ParquetNeo, columns | 86 ms | 106 ms | 52 MB |
| Parquet.Net, columns | 85 ms | 117 ms | 28 MB |
| ParquetSharp, columns | 115 ms | 134 ms | 13 MB |
| ParquetNeo, serializer | 198 ms | 181 ms | 188 MB |
| Parquet.Net, serializer | 143 ms | 182 ms | 125 MB |

### Reading

| | Uncompressed | Snappy | Allocated |
|---|---:|---:|---:|
| ParquetNeo, columns | 23 ms | 43 ms | 34 MB |
| Parquet.Net, columns | 107 ms | 99 ms | 77 MB |
| ParquetSharp, columns | 56 ms | 86 ms | 36 MB |
| ParquetNeo, serializer | 186 ms | 166 ms | 162 MB |
| Parquet.Net, serializer | 309 ms | 204 ms | 148 MB |

### Notes on the numbers

- They come from one machine, one dataset and a short BenchmarkDotNet job. Differences of 10 to 20 percent between two rows
  are within the noise (the error column of the BenchmarkDotNet output is up to about 30 ms for writes, and the
  Parquet.Net serializer read has an error larger than its mean). The large differences, such as reading through columns, are not.
- Writing through columns is on a par with Parquet.Net and ahead of ParquetSharp. The serializer is the weak spot: roughly
  twice the time of the column API, and not ahead of Parquet.Net's serializer when writing.
- ParquetSharp, being native code behind a managed interface, allocates the least; ParquetNeo allocates more than both when
  writing through columns.

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

## Not done yet

Left for a follow-up, with what is known so far:

- **The serializer** spends its time around the data and not in the encoders. A first profile of writing points at the
  shredding of each row (several virtual and delegate calls per value), the buffers that collect a column, and runtime lookups
  in generic code shared between reference types; these are leads to confirm, not findings. Reading is a matter of building
  the objects and similar overhead.
- **Parallel column encoding.** The columns of a row group are independent, and the writer encodes them one after the other.
  With the cost of the string column above, encoding the columns in parallel could bring a table like this one down to the time
  of its slowest column.
- **Allocation of the column writer** is higher than that of the other libraries (the pages are held before they are written).
- **Giving up a dictionary early.** A column of unique values (an identifier) fills a dictionary up to its size limit before the
  writer gives it up. Deciding sooner would be faster, but changes which files are written for such columns.
- **Asynchronous input and output** has not been tuned.
