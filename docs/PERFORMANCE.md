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

The competitors are driven the way their own documentation makes fastest, and each choice was timed against the alternatives
(1M rows, same machine) before it was kept:

- **ParquetSharp, reading:** a native buffer (`BufferReader` over the pinned bytes) and its physical column readers for the
  numbers (49 ms with the logical readers over a .NET stream, 38 ms with these, and 36 MB allocated against 8.5 MB). The text
  columns have no physical form that makes strings, so they use the logical reader. A physical `ReadBatch` can return fewer values
  than asked for, so it is called until every row is read (the benchmark checks the count).
- **Parquet.Net, reading:** `ReadRawAsync` for the nullable double, which returns the values and their definition levels the way
  ParquetNeo does, instead of `ReadAsync`, which expands them to `double?` (62–76 ms down to 46–52 ms).
- **ParquetSharp, writing:** nothing was faster than the logical writers: physical writers, a native output buffer and switching
  size statistics off all took 101–105 ms against 103 ms. The column descriptors are made once, outside the timed method.
- **Parquet.Net, writing:** nothing was faster either: neither the pool limits, the dictionary settings (none of them make it use
  a dictionary), nor reusing the fields changed its 73 ms.
- Every library allocates the arrays it reads into inside the timed method, so none is helped by buffers made in advance.

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

| | Uncompressed | Snappy | Allocated | File (uncompressed / Snappy) |
|---|---:|---:|---:|---:|
| ParquetNeo, columns | 67 ms | 89 ms | 18 KB | 16.2 / 8.8 MB |
| ParquetNeo, columns, no dictionary | 38 ms | 65 ms | 13 KB | 31.3 / 10.9 MB |
| Parquet.Net, columns | 88 ms | 123 ms | 28 MB | 32.7 / 11.0 MB |
| ParquetSharp, columns | 112 ms | 128 ms | 13 MB | 15.7 / 8.2 MB |
| ParquetNeo, serializer | 129 ms | 148 ms | 36 MB (20 KB since the pooled buffers, below) | 16.2 / 8.8 MB |
| ParquetNeo, serializer, 4 threads | 103 ms | 107 ms | 36 MB | same |
| Parquet.Net, serializer | 140 ms | 166 ms | 128 MB | 32.7 / 11.0 MB |

Parquet.Net's column writer writes every column PLAIN, with no dictionary (its dictionary options do not change that), which is
cheaper per row and makes its files about twice the size of those of the libraries that use a dictionary. The like-for-like
comparison with it is the row of ParquetNeo without a dictionary, which writes the same kind of file (31 MB against 33 MB) in
less than half the time uncompressed. `dotnet run -c Release --project benchmarks/ParquetNeo.Benchmarks -- --sizes` prints the
sizes.

### Reading

| | Uncompressed | Snappy | Allocated |
|---|---:|---:|---:|
| ParquetNeo, columns | 22 ms | 44 ms | 34 MB |
| Parquet.Net, columns | 88 ms | 94 ms | 73–92 MB |
| ParquetSharp, columns | 51 ms | 80 ms | 30 MB |
| ParquetNeo, serializer | 130 ms | 145 ms | 106 MB |
| Parquet.Net, serializer | 210 ms | 180 ms | 148–167 MB |

### Notes on the numbers

- They come from one machine, one dataset and a short BenchmarkDotNet job. Differences of 10 to 20 percent between two rows
  are within the noise (the error column of the BenchmarkDotNet output is up to about 15 ms for writes, and the
  Parquet.Net serializer read has an error close to its mean). The large differences, such as reading through columns, are not.
- Writing through columns is ahead of both libraries, and the like-for-like row (no dictionary, the file Parquet.Net writes)
  is more than twice as fast as Parquet.Net uncompressed. The serializer takes about twice as long as the column API; it is
  ahead of Parquet.Net's serializer in both directions.
- ParquetNeo's writer takes its scratch space from the pool, so it allocates next to nothing once it is warm: the figure is for
  a file written after others, as in a benchmark or a service. A single file written in a fresh process still allocates for the
  pool to grow the first time.

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

A third round went to writing through columns, which allocated 52 MB for the table (against 28 MB and 13 MB for the other libraries)
and was slower than Parquet.Net on at least one laptop. Writing each column on its own showed where it went: the two columns of
unique values (an identifier and an amount) spent 19 ms and 25 MB each building a dictionary of 131,000 entries that the writer
then gave up, and the string column spent most of its 47 ms in its dictionary.

- **The dictionaries are open-addressing tables over pooled arrays**, for numbers (`FixedDictionary`) and for byte arrays and
  strings (`BinaryDictionary`: the values are copied one after the other into one array, and an entry is its offset, length and
  hash, so most mismatches are told from the hash alone). Entries are numbered in order of first appearance, so the file is the
  same. The hash is seeded randomly for every dictionary, so that input chosen by an attacker cannot be crafted to collide.
- **The scratch buffers of a writer come from the pool** (`PooledBufferWriter`, in place of `ArrayBufferWriter`, which grows by
  doubling and is made anew for every file) and go back when the file is closed.
- **A dictionary index is appended with one call** instead of a request for room and an advance.

The unique `long` column went from 19 ms and 25 MB to 5 ms and nothing; the unique `double` column from 19 ms and 26 MB to 12 ms;
the string column from 47 ms to 37 ms. The table through columns went from 86 ms and 52 MB to 67–71 ms and 18 KB.

A fourth round compiled the rows. A row type whose properties are all single columns (no nested objects, collections or maps,
and, for reading, created by its parameterless constructor) is written and read by one function generated for it with expression
trees, instead of a loop over a tree of small objects that each make a virtual call and call a delegate per value:

- **Writing**: the function reads every property of the row and adds it to its column buffer (`LeafBuffer`), with the null
  checks that the type of the property needs (none for a value type, `HasValue` for a nullable one, a reference check for a
  reference type, and the same error naming the property and the row when a required column is null).
- **Reading**: the function creates the instance, takes the next value of every column and sets the property, or sets it to null
  (or raises the same error naming the row, when it cannot be).
- Any other shape (nested objects, lists, maps, types built by a constructor) keeps
  the general path, which does the same thing. The files written are the same, byte for byte.

On the 1,000,000-row table of five columns the serializer writes in about 118 ms where it took 142 ms, and reads in about 65 ms where
it took about 90 ms (the floor, objects made by hand from arrays read through the column API, is 39 ms).

A fifth round gave up on dictionaries sooner, as an option because it changes which files are written. A column of mostly
different values (an identifier, a measurement) fills a dictionary up to its size limit (131,000 entries for a `long`) before the
writer gives it up, which cost 5 ms for the identifier column against 1.4 ms written plain, and left a file 7% bigger because of
the dictionary page and the pages held for it.

- **`DictionaryMode`** says, for a column, how it is dictionary encoded: `Standard` (the default, as before), `Adaptive` or
  `Disabled`. It is set for chosen columns with `ParquetWriterOptions.ColumnDictionaries` (by column path, like
  `ColumnEncodings`) or, in the serializer, with `[ParquetDictionary(DictionaryMode.Adaptive)]` on the property; the options win
  over the attribute, and `DictionaryEnabled = false` turns every dictionary off whatever a column says.
- **A size limit per column**: `DictionarySettings` carries the mode and an optional `SizeLimit` that replaces
  `DictionaryPageSizeLimit` for that column (`SizeLimit = ` on `[ParquetDictionary]`, or a `DictionarySettings` in
  `ColumnDictionaries`; a `DictionaryMode` alone converts to settings). Raise it for a column with few distinct values that are large
  (long texts, urls), whose dictionary would be dropped half way through; the dictionary and its pages are held in memory until the
  column is complete, so it costs memory.
- **`Adaptive`** looks at the first 32 KiB of the values of the column (4,096 longs); if more than nine in ten are different, the
  dictionary is dropped there and the rest is written plain. The dictionary page and page written for those first values stay in
  the file (32 KB for a long), which readers handle like any other column that gave up its dictionary. A column that repeats is
  unchanged, byte for byte.

On the benchmark table (identifier and amount adaptive; one laptop that was busy, so the ratios are what matters) writing the
columns takes 0.80 of the time it did, 1 MB less in the file (16.2 MB to 15.2 MB uncompressed, 8.8 MB to 7.8 MB with Snappy). Written
plain, without any dictionary, it takes 0.55 of the time, but the file is twice the size: the remaining difference is the
dictionary of the string column.

A sixth round went to the string dictionary, whose loop was the hottest in a column of text:

- **The hash is one 64 by 64 bit multiplication** for values up to 16 bytes (two overlapping words, in the manner of wyhash) and
  one per 16 bytes beyond that, in place of a rotate and multiply per 8 bytes. It is still seeded randomly for every dictionary,
  and the output does not depend on it.
- **ASCII text is narrowed with `Ascii.FromUtf16`**, a vector at a time, and only text that is not ASCII goes through the UTF-8
  transcoder.
- **One loop adds a page of strings to the dictionary**, keeping what changes in locals (the bytes of the page, the count of values,
  the dictionary, the statistics) instead of fields that every value reads and writes.

The string column of 1,000,000 rows (900,000 values, 5,000 of them different) went from 26–28 ms to 24–25 ms; the table of five
columns by 2 to 3 ms of 43 (the measurements of this laptop vary by that much). The files are the same, byte for byte.

A seventh round made the parallel form public: `RowGroupWriter.WriteColumns(actions, maxDegreeOfParallelism)` (and an overload that
uses the processors, and a cancellation token) takes one action per column, encodes them on different threads and copies the
encoded columns to the file in the order of the schema, which is what the serializer and the ADO.NET writer already did. The
file is the same, byte for byte, as the one written a column at a time (tested for several degrees, with and without
compression). The encoded group is held in memory until every column is done, which `NextColumn` does not need.

On the benchmark table, through the column API with four threads: 48.7 ms becomes 33.0 ms uncompressed (0.68) and 73.1 ms becomes
34.1 ms with Snappy (0.47); the time is that of the slowest column, here the strings.

An eighth round brought the compiled functions to rows that are not flat. The function generated for an object now handles every
kind of property: a single column goes straight to its buffer (or is taken from its column), and an object, collection or
dictionary is handed to its shredder (or assembler), which is called directly from the generated code instead of through a delegate
that reads or sets the property. An object anywhere in the row is compiled, with the definition and repetition levels as arguments
because it can be anywhere in the row. When reading, a list that is an array or a `List<T>` collects its elements in a scratch buffer
of the read and is then made once, with exactly the room it needs, in place of a list that grows by doubling (and for an array, is
copied again by `ToArray`); the scratch belongs to the read, not to the assembler, since assemblers are shared by every read of a row
type.

On 200,000 orders with an optional customer, an address, a list of tags, an array of scores and a list of order lines, written to a
memory stream and read back (the `NestedSerializerBenchmarks` class), the time to write went from 117–131 ms to 104–118 ms, the time
to read from 78–91 ms to 71–78 ms, and the memory allocated when reading from 144 MB to 124 MB. Reading is bound by the garbage
collector (the objects it creates), so the time improves less than the allocation does. The files are the same, byte for byte.

A ninth round pooled the column buffers of the serializer. A row group of a million rows takes about 35 MB of arrays (the values and
the levels of each column), and they were allocated for every file written: on the large object heap, so every file caused a full
garbage collection or more. A benchmark run on a laptop showed it as Gen2 collections for the serializer with four threads (one for
every 16 files) that the column API does not have. `LeafBuffer` now takes its arrays from the shared array pool and gives them
back when the serializer is disposed (cleared, for arrays of references, so that the pool does not keep the rows' strings alive),
and so does the buffer of the ADO.NET writer. The pool hands out arrays that are not cleared and may be larger than asked, which
nothing here depends on (checked by writing sparse rows into arrays a run of full ones left behind: the file is the same bytes).

Per file of the benchmark table, the serializer allocates 20 KB (it was 36 MB) and 27 KB with four threads, with no garbage
collection at all in a steady state; the time with four threads went from about 72 ms to 62 ms on the machine of the tests, and
the time with one thread is unchanged within the noise of the measurements. The cost is that the pool keeps the arrays between
files (it releases them when memory is short), so a process that writes one big file and then nothing holds on to that much memory
for a while.

A tenth round tuned asynchronous output, which had two costs: the encoded row group was staged in a `MemoryStream` that doubles on the
large object heap for every file, and the next row group was not collected or encoded until the write of the one before had
ended, so the time was that of encoding plus that of writing. Now:

- **The staging area is pooled** (`StagingStream`): the bytes of a row group are encoded into arrays from the array pool, and the
  buffer that follows starts as big as the one before, so a file does not grow its staging by doubling and copying.
- **A row group is written while the next is collected and encoded** (`PipelinedWriter`): `WriteAsync` of the stream is started and not
  awaited until the next row group is ready to be handed over, since a stream takes one write at a time. At most two row groups
  are in memory. A failed or cancelled write surfaces at the next hand-over or at the end, and the memory of a write that is
  going is not given back to the pool until the write ends. The same for `SerializeAsync` of asynchronous rows and for the
  asynchronous ADO.NET writer.
- **The synchronous ADO.NET writer writes straight to the stream**, as the synchronous serializer already did, instead of staging
  each row group in memory first.

Four row groups of the benchmark table (4,000,000 rows, 64.7 MB), to a destination that takes the time a given speed would:

| Destination | before | after |
|---|---|---|
| memory stream | 394 ms | 322 ms |
| 400 MB/s | 535 ms | 360 ms |
| 200 MB/s | 663 ms | 435 ms |
| 100 MB/s | 1,011 ms | 756 ms |

With a destination that takes no time the gain comes from the pooled staging (97 MB allocated on the calling thread becomes
35 MB, and the pooled column buffers of the round before take that to nearly nothing); with a slow one the encoding hides behind the writes (at 100 MB/s the writes alone take about 650 ms). Only the row groups
overlap: a file of a single row group has nothing to overlap, so `RowGroupSize` should be a fraction of the rows for a slow
destination. `AsyncWriteComparison` reproduces it with BenchmarkDotNet.

## Not done yet

Left for a follow-up, with what is known so far:

- **Rows built by a constructor.** Types that are created by a constructor with parameters (records, for example) still read through
  the general path, which holds the values in boxed slots until the constructor is called; the compiled function could call the
  constructor with the values straight from the columns.
- **Collections and dictionaries inside rows** are walked by their shredders and assemblers (a virtual call for each element); only
  the properties around them are compiled. Maps are still created empty and added to.
- **Encoding strings further.** The dictionary encoded string column is still the slowest column to write (about 24 ms of the
  42 ms for the table, 27 ns for each of 900,000 values). The profile is flat: reading the string objects (they are all over
  the heap), narrowing them to bytes, hashing, the lookup (a table, an entry, then the bytes) and packing the indexes each take a
  few nanoseconds. A layout of the entries that puts offset, length and hash side by side was tried and did not show a gain.
  What would help most is not doing the work for a value: a cache by string reference helps data that shares its strings, but
  needs a hash of the reference, which the runtime does not give cheaply.
- **Asynchronous input.** `DeserializeAllAsync` reads the footer and the byte ranges of the columns the row type needs; it does
  not read the next row group while the rows of one are assembled, as the writer now does in the other direction.
