# ParquetNeo – Implementation Plan

A from-scratch, high-performance Parquet reader/writer for .NET (C#), built from the
spec in [`specs/`](../specs) (`File Format _ Parquet.html` and `parquet.thrift`).

## Goals

- **Fast first, easy second.** Zero per-value allocations on hot paths, span-based
  APIs, pooled buffers, SIMD where it pays off, async I/O.
- **Two layers of usage**
  - **Low level** (`ParquetNeo`): `ParquetFileReader` / `ParquetFileWriter` work on
    row groups and column chunks. Columns are read/written as `Span<T>` plus
    definition/repetition levels. Metadata and statistics are exposed.
  - **High level** (`ParquetNeo.Serialization`, which builds on `ParquetNeo`): `ParquetSerializer.SerializeAsync<T>(...)` and
    `DeserializeAsync<T>(...)` / `IAsyncEnumerable<T>` over plain DTOs. Built on top of the
    low-level layer; users never touch columns.
- **Minimal dependencies.** Core depends only on the BCL. Thrift compact protocol and all
  encodings are hand-written. Gzip/Brotli via `System.IO.Compression`.
  Snappy (via the managed Snappier library), Zstd and LZ4_RAW live in optional add-on packages.
- **Tested.** Every step ships with TUnit unit tests (see [Testing](#testing)).

## Decisions / assumptions (change in review if you disagree)

| Topic | Default |
|---|---|
| Target framework | `net10.0` only (matches the existing scaffold) |
| Encryption (modular) | Out of scope for v1 |
| Variant, Variant shredding, Geospatial, ALP | After the core is stable (step 17) |
| LZ4 (Hadoop-framed) / LZO | Not supported. `LZ4_RAW` is |
| INT96 | Read-only (deprecated but common in the wild) |
| Interop fixtures | Files from `apache/parquet-testing` committed under `tests/`; `pyarrow` used to generate extra fixtures only if available |

## Spec points that shape the design

- Footer may carry extensions at Thrift field id `32767`; readers must skip unknown
  Thrift fields and tolerate trailing extension bytes. `PAR3` / FlatBuffers footers are an
  optional later extra.
- Writers must emit both `LogicalType` and legacy `ConvertedType`; readers fall back to
  `ConvertedType` when `LogicalType` is absent; unknown logical types degrade to the
  physical type.
- Dictionary page: at most one per column chunk and always first.
- Nulls live only in definition levels; required / non-nested columns skip levels entirely.
- Pages may carry optional CRC32 checksums. Gzip readers must accept multi-member streams.
- Recommended defaults: ~8 KB data pages, 512 MB–1 GB row groups – both configurable.

## Working routine

For every step below:

1. A branch is created for the step (`step-NN-short-name`).
2. Work is done there, including tests.
3. A pull request is opened against `main`.
4. Review and comments happen on GitHub; fixes are pushed to the same branch.

Each step is sized to be reviewable on its own (roughly 300–800 lines of production code
plus tests).

## Steps

### Milestone 1 – Foundations

| # | Step | Deliverable |
|---|---|---|
| 0 | **Scaffold** | Replace xunit with TUnit (Microsoft.Testing.Platform), remove `Class1.cs`, add `.editorconfig`, GitHub Actions build + test workflow, empty BenchmarkDotNet project, README skeleton. |
| 1 | **Thrift compact protocol + metadata** | Span-based compact protocol reader/writer for the subset Parquet needs; generated-by-hand model of `FileMetaData`, `RowGroup`, `ColumnChunk`, `ColumnMetaData`, `PageHeader`, `SchemaElement`, `Statistics`, `LogicalType`, etc. from `specs/parquet.thrift`. Unknown-field skipping and field 32767. Tests: golden bytes, round-trips, metadata from a real file. |
| 2 | **Schema model** | Schema tree (physical type, repetition, logical annotations); conversion to/from the flattened `SchemaElement` list; max definition/repetition level computation. |

### Milestone 2 – Flat columns, read and write

| # | Step | Deliverable |
|---|---|---|
| 3 | **Level & primitive encodings** | RLE/bit-packed hybrid (SIMD unpack/pack), `PLAIN` for all physical types, boolean bit-packing. |
| 4 | **Compression** | `ICodec` abstraction; Uncompressed, Gzip (multi-member read) and Brotli built in; Snappy (Snappier), Zstd and LZ4_RAW in add-on packages. |
| 5 | **Low-level writer (flat)** | `PAR1` magic, data page v1, required and optional flat columns, `PLAIN`, row-group builder, footer. |
| 6 | **Low-level reader (flat)** | Footer parsing, column chunk/page iteration, read into spans. Tests: files from step 5 and external fixtures. |
| 7 | **Dictionary encoding** | Dictionary page write/read, `RLE_DICTIONARY` (and legacy `PLAIN_DICTIONARY` on read), fallback to `PLAIN` when the dictionary grows too large. |
| 7b | **Statistics** | Min/max/null-count statistics per column chunk (type-correct ordering, exact flags), exposed on the metadata. |

### Milestone 3 – Full format coverage

| # | Step | Deliverable |
|---|---|---|
| 8 | **Nested columns** | Repetition levels in the low-level reader and writer: nested schemas, leaf columns written and read with definition and repetition levels, pages that never split a row, row and entry counts. |
| 8b | **Nested assembly** | Converting between levels and offsets/validity for structs, lists and maps (shredding and assembly), standard LIST/MAP schema builders, and recognition of the legacy LIST/MAP layouts on read. |
| 9 | **Advanced encodings** | `DELTA_BINARY_PACKED`, `DELTA_LENGTH_BYTE_ARRAY`, `DELTA_BYTE_ARRAY`, `BYTE_STREAM_SPLIT` in the reader and writer; the writer takes them per column (`ColumnEncodings`, PLAIN by default). |
| 9b | **Data page v2 writing** | The writer can produce data page v2 (levels uncompressed, `is_compressed`, `num_rows`/`num_nulls`), chosen by an option; the reader already reads it. |
| 10 | **Logical types (numbers, time, identifiers)** | Typed read and write overloads on the column reader and writer: sized and unsigned integers, `DateOnly`, `TimeOnly`, `DateTime`, `DateTimeOffset`, `Guid`, `Half`; checks of the column's logical type; `ConvertedType` written alongside. |
| 10b | **Logical types (decimal, text, INT96)** | `decimal` reads and writes for DECIMAL columns of every physical type (scale up to 28, exact: digits are never lost), `ReadStrings` for STRING/ENUM/JSON (and the annotation check on `WriteStrings`), read-only INT96 timestamps as `DateTime`/`DateTimeOffset`, and the fallback to the physical type for unknown logical types. Larger decimals (`BigInteger`, wide scales) are left to the high-level API. |

### Milestone 4 – High-level DTO API

| # | Step | Deliverable |
|---|---|---|
| 11 | **High-level write (flat rows)** | `ParquetSerializer.Serialize` / `SerializeAsync` (sync, `IEnumerable<T>` and `IAsyncEnumerable<T>`) writing rows as a Parquet file: a schema inferred from the public properties (primitives, `decimal`, strings, binary, `Guid`, date and time types, `Nullable<T>` and nullable reference types), attributes to rename, order, ignore, and set decimal precision, time units and encodings, and rows batched into row groups that are encoded and written as they fill. |
| 11b | **High-level write (nested)** | Nested objects, collections (arrays, `List<T>`, any `IEnumerable<T>`), dictionaries and enums as properties, written as groups, LISTs and MAPs through the Dremel levels; nullability from nullable reference types at every level; attributes reaching the elements, keys and values. |
| 12 | **High-level read (flat rows)** | `ParquetSerializer.Deserialize` / `DeserializeAll` / `DeserializeAsync` / `DeserializeAllAsync` reading rows into DTOs one row group at a time: columns matched by property name, only those columns read (and, asynchronously, only their byte ranges fetched), clear `SchemaMismatchException`s for missing or mismatched columns, DTOs created through a parameterless constructor or a record-style constructor, enums by name. |
| 12b | **High-level read (nested)** | Nested objects, collections (arrays, `List<T>`, sets and other `ICollection<T>`) and dictionaries read from the definition and repetition levels of the file, the reverse of the shredding in 11b: the file's own nodes decide where objects and lists start (so required or optional groups, and the older list layouts, read the same), shapes that do not fit are `SchemaMismatchException`s, and only the nested columns the row type names are read. |
| 12c | **Column types** | Refactor, no change in behavior: every .NET type that has a column of its own is a `ColumnType<T>` (`Int32ColumnType`, `DateTimeColumnType`, `DecimalColumnType`, `EnumColumnType<T>`, ...) holding all of its logic in one place: the column it creates, which columns of a file fit it, and the batch conversion to and from the physical type. The typed `ReadBatch`/`WriteBatch` overloads of the column reader and writer, and the serializer's shredders, assemblers and buffers, all use it; `ColumnTypeRegistry` finds them, and registering your own teaches the serializer a new type. Column attributes become `ColumnSettings`. |
| 12d | **ADO.NET (`IDataReader`)** | `ParquetDataWriter.Write`/`WriteAsync` write the rows of any `IDataReader` as a Parquet file (column types from `GetFieldType`, nullability and decimal precision from the schema table, row groups of a set size, so memory is bounded), and `ParquetDataReader` is a `DbDataReader` over a Parquet file for `SqlBulkCopy`, `DataTable.Load` and the like: the top-level columns that hold one value per row, typed by their logical type, one row group in memory at a time. Nested columns are not exposed yet. |

### Milestone 5 – Performance, extras, release

| # | Step | Deliverable |
|---|---|---|
| 13 | **Performance pass (measure, strings)** | Comparison benchmarks against Parquet.Net and ParquetSharp (`benchmarks/ParquetNeo.Benchmarks/Comparison`), a per-column breakdown, and the string fast paths they led to: text dictionaries decoded once per entry, one-pass UTF-8 encoding, statistics of a dictionary column from its new entries only. Results and open items in [PERFORMANCE.md](PERFORMANCE.md). |
| 13b | **Performance pass (serializer, parallelism)** | The serializer after the profile of step 13: columns of a row group encoded in parallel (`MaxDegreeOfParallelism`, same bytes whatever the degree), column buffers sized up front, a direct write to the stream, one delegate call per property, and lists and arrays sized from the file when reading (capped, since the file's numbers are not trusted). Serializer write 198 → 143 ms (128 ms with four threads), read 186 → 125 ms. |
| 13c | **Performance pass (column writer)** | Writing through columns without allocating: dictionaries for numbers and byte arrays as open-addressing tables over pooled arrays, and pooled scratch buffers for the writer, both with the same bytes in the file. 52 MB → 18 KB allocated and 86 → 71 ms for the benchmark table; the unique-valued columns went from about 19 ms and 25 MB each to 5 and 12 ms and nothing. |
| 13d | **Performance pass (the rest)** | One compiled function per object type instead of walking shredders and assemblers (done for flat rows, then for objects, collections and dictionaries inside rows; not yet for types built by a constructor), a public parallel form for the column API (done: `RowGroupWriter.WriteColumns`), faster string dictionaries (done: hash, ASCII, one loop), an earlier decision to give up a dictionary (done as the per-column `DictionaryMode` option), and asynchronous I/O tuning (done: a pooled staging area and the next row group encoded while the last is written). See [PERFORMANCE.md](PERFORMANCE.md). |
| 14 | **Source generator** | AOT/trimming-safe DTO mapping replacing the reflection path; same tests run against both paths. Opt-in with `[ParquetSerializable]` on the DTO (or `[assembly: ParquetSerializable(typeof(X))]` for a type you do not own); the generator registers the mapping when the assembly loads, so `Serialize<T>`/`Deserialize<T>` stay as they are, and a type with no mapping keeps the reflection path, which is not trim or AOT safe. Done in small steps: **14a foundation**, **14b flat rows**, **14c nested rows and collections**, **14d constructor-built types** (all below). |
| 14a | **AOT foundation (no generator yet)** | The library is `IsAotCompatible` with no warnings: column buffers and data are made through a visitor on `ColumnType` instead of `MakeGenericType`, enums are registered with `ColumnTypeRegistry.RegisterEnum<T>()` where types cannot be made at run time, the struct shredder and assembler take the function that shreds or builds a row instead of compiling it, and the reflection builders (the only code that reflects or compiles) are annotated `RequiresUnreferencedCode`/`RequiresDynamicCode` and refused under Native AOT with an explanation. `RowModel<T>.Register`/`ReadModel<T>.Register` is where a generated mapping plugs in. `tests/ParquetNeo.AotSmoke` is published with Native AOT in CI and runs the column API and the serializer (Gzip, Brotli) over a mapping written by hand, which the tests compare byte for byte with the reflection path. |
| 14b | **Source generator (flat rows)** | `ParquetNeo.Generator` (a Roslyn incremental generator, netstandard2.0) and the public `ParquetSerializableAttribute`. For each marked type it writes straight-line shred and assemble code and registers it from a module initializer through `MappingRegistry` (the mapping itself is made the first time the serializer needs it): columns, their order, nullability (from the nullable annotations), enums, and the settings attributes (`ParquetColumn`, `ParquetIgnore`, `ParquetDecimal`, `ParquetTime`, `ParquetTimestamp`, `ParquetEncoding`, `ParquetDictionary`) all come out as the reflection path works them out. Types it cannot map yet (object, collection or dictionary properties, constructor-built types, generic or inaccessible types, attributes that do not suit their property) get warning PNEO001 and keep using reflection; a type that can be written but not read gets PNEO002. The runtime side is a small public surface (`WriteMappingBuilder<T>`, `ReadMappingBuilder<T>`, `RowSink`, `RowSource`) that wraps the internal shredders and assemblers. Tests: the generator's own project (output shape, diagnostics, the list of column types against the library's), and in `ParquetNeo.Tests` a type with every kind of column that gets a generated mapping next to a twin that does not, which must write byte-identical files and read the same rows. The Native AOT smoke test uses the generated mapping. Not yet: packaging the generator inside the `ParquetNeo` NuGet package (step 16). |
| 14c | **Source generator (nested rows and collections)** | The generator maps objects and structs of any depth, and arrays, lists, sets, other `ICollection<T>`s and dictionaries of anything (including collections of collections, objects in dictionaries, nullable elements and values), each object type described once however often it is used. The public builders for generated code became stack-based (`Leaf`, `BeginObject`, `BeginList`/`BeginNullableList`, `BeginMap`/`BeginMapOfNullable`, `End`) over the same internal shredders and assemblers, so the schema, levels and messages are the ones of the reflection path; the generated straight-line code per object type reads its properties by column number relative to the object, since the object can be anywhere in the row. Collections that are not made from what was read (sets, collection classes) get a generated `create` function. Types it still leaves to reflection: constructor-built types (a type with an object like that inside can be written but not read, PNEO002), and the rest as in 14b. Tests: a row with every shape (objects and structs, nullable and not, lists, arrays, sets, collection classes, dictionaries, three levels deep, nulls and empties) written next to its reflection twin gives byte-identical files with several codecs and row-group sizes and the same rows and errors; the Native AOT smoke test round-trips a nested row with all of them. Speed against reflection (`GeneratedMappingWriteBenchmarks`, `GeneratedMappingReadBenchmarks`, flat and nested rows, see [PERFORMANCE.md](PERFORMANCE.md)): the same within the error of the runs, with the same allocation. |
| 14d | **Source generator (constructor-built types)** | A type without a public constructor without parameters (a record, a struct with get-only properties, a class with a constructor) is created by calling its public constructor that has the most parameters that are all properties of the same names (without regard to case) and types, as the reflection path chooses; the properties that are not parameters are set by their setters (`init` included) in the same expression. The generated code reads each parameter from its column straight into the call, with no boxed slots. A type with no constructor that fits (or an object inside it with none) can be written but not read, `PNEO002`. Tests: records with records, structs and lists inside, a class with several constructors and parameters of another case, a struct, and the message for a null in a parameter, each beside a reflection twin with byte-identical files; the Native AOT smoke test round-trips a record with a list of records. Reading a record with a generated mapping takes 20 ms against 47 ms for reflection (100,000 rows, 19 MB allocated against 35 MB), see [PERFORMANCE.md](PERFORMANCE.md). This completes step 14. |
| 15 | **Read-side extras** | Row-group skipping via statistics, page index, bloom filters, CRC32 page checksums, optional `PAR3` / FlatBuffers footer; done in the parts below. |
| 15a | **Row-group skipping** | `ParquetFilter` (`Column("x").GreaterThan(5) & …`; comparisons, `Between`, `In`, null tests, `And`/`Or`/`Not`) checked against the statistics of each row group by `ParquetFileReader.GetMatchingRowGroups` and `RowGroupReader.MayMatch`, and the `RowGroupFilter` option of `ParquetSerializer` (all four methods, the asynchronous ones do not fetch the bytes of a skipped row group) and `ParquetDataReader`. A filter only prunes: it never skips a row group with a row that satisfies it (checked against a plain evaluation of random data and filters), and the rows of the row groups it keeps are not filtered. Values compare as the column type defines (unsigned integers, decimals, dates, times and timestamps by their units, text by UTF-8 bytes, NaNs and nulls as in C#); enums and `Guid`s only for equality; columns in collections, missing columns and missing statistics keep the row group. |
| 15b | **LINQ over files** | `ParquetNeo.Linq`, a separate project (not AOT or trim safe, and says so) with its own tests: `ParquetDataset<T>`, an `IQueryable<T>` over a file or a directory of files (local files first). `Where` is translated to a `ParquetFilter` to skip row groups and files from their footers, and the whole predicate is applied to the rows that are read; `Select` reads only the columns it needs (anonymous types and records are read by their constructors); `OrderBy`/`ThenBy`, `Skip`/`Take`, `First*`/`Single*`, `Any` and `Count` (from the statistics when nothing filters); asynchronous enumeration; results in whichever order is fastest when there is no `OrderBy`. |
| 15c | **Checksums and bloom filters** | CRC32 of pages: written by an option, verified on read by an option, with an exception that names the page. Bloom filters: read (and written for chosen columns) and used by equality filters to skip row groups the statistics cannot rule out. |
| 15d | **Page index** | `ColumnIndex` and `OffsetIndex` read and written, so that a filter skips pages inside a row group, not only row groups. |
| 15e | **`PAR3` footer** | Optional: the `PAR3` / FlatBuffers footer, last, once the format is settled. |

Possible enhancements after step 15, not planned in detail:

- **Hive-style partition folders** (`year=2024/month=05/…`) as virtual columns of `ParquetDataset<T>`, so that a filter on them skips whole files without opening them.
- **A storage abstraction** (listing files and opening a stream or a byte range of one) so that datasets can be read from blob storage as well as from local files; the datasets use local files until then.
| 16 | **Release** | README with examples, XML docs, NuGet packaging. |
| 17 | **Preview & new types** | ALP encoding, then Variant / Variant shredding and Geospatial (GEOMETRY/GEOGRAPHY). |

Steps 0–7b are the minimum for flat tables, 8 and 8b are needed for real-world files, and 11–12
deliver the DTO API. Steps 13–15 may be reordered based on benchmark results.

## Testing

- Framework: **TUnit**. **Every library project has a test project of its own**
  (`tests/<Project>.Tests`: `ParquetNeo.Tests`, `ParquetNeo.Serialization.Tests`, `ParquetNeo.Enums.Tests`,
  `ParquetNeo.Compression.Snappy.Tests`, ...), with the fixtures it needs in its own `Fixtures` folder.
  Test code that several of them share (the checks every codec must pass, the data they run on) is in
  `tests/ParquetNeo.Tests.Common`, a library with no tests. A test belongs to the project of the code it is about;
  a test that exercises several projects together (the writer with every codec, for example) lives with the
  project that uses the others.
- Unit tests for every encoder/decoder: golden-byte vectors from the spec, edge cases
  (empty, single value, run boundaries, bit widths 0–64), and randomized round-trips.
- Write → read round-trip tests for every physical/logical type, encoding and codec.
- Interop tests: read files produced by other implementations (`apache/parquet-testing`),
  and where tooling allows, verify files we write with an independent reader.
- Allocation tests on hot paths (e.g. asserting no allocations per value).
- `TreatWarningsAsErrors` stays on; CI runs build + tests on every PR.
