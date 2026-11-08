# ParquetNeo – Implementation Plan

A from-scratch, high-performance Parquet reader/writer for .NET (C#), built from the
spec in [`specs/`](../specs) (`File Format _ Parquet.html` and `parquet.thrift`).

## Goals

- **Fast first, easy second.** Zero per-value allocations on hot paths, span-based
  APIs, pooled buffers, SIMD where it pays off, async I/O.
- **Two layers of usage**
  - **Low level** (`ParquetNeo.Core`): `ParquetFileReader` / `ParquetFileWriter` work on
    row groups and column chunks. Columns are read/written as `Span<T>` plus
    definition/repetition levels. Metadata and statistics are exposed.
  - **High level** (`ParquetNeo`): `ParquetSerializer.SerializeAsync<T>(...)` and
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
| 13d | **Performance pass (the rest)** | Compiling one function per row type instead of walking shredders and assemblers, a public parallel form for the column API, faster string dictionaries, an earlier decision to give up a dictionary, and asynchronous I/O tuning. See [PERFORMANCE.md](PERFORMANCE.md). |
| 14 | **Source generator** | AOT/trimming-safe DTO mapping replacing the reflection path; same tests run against both paths. |
| 15 | **Read-side extras** | Row-group skipping via statistics, page index, bloom filters, CRC32 page checksums, optional `PAR3` / FlatBuffers footer. |
| 16 | **Release** | README with examples, XML docs, NuGet packaging. |
| 17 | **Preview & new types** | ALP encoding, then Variant / Variant shredding and Geospatial (GEOMETRY/GEOGRAPHY). |

Steps 0–7b are the minimum for flat tables, 8 and 8b are needed for real-world files, and 11–12
deliver the DTO API. Steps 13–15 may be reordered based on benchmark results.

## Testing

- Framework: **TUnit**, one test project per library project (`tests/ParquetNeo.Tests`, plus
  add-on test projects as they appear).
- Unit tests for every encoder/decoder: golden-byte vectors from the spec, edge cases
  (empty, single value, run boundaries, bit widths 0–64), and randomized round-trips.
- Write → read round-trip tests for every physical/logical type, encoding and codec.
- Interop tests: read files produced by other implementations (`apache/parquet-testing`),
  and where tooling allows, verify files we write with an independent reader.
- Allocation tests on hot paths (e.g. asserting no allocations per value).
- `TreatWarningsAsErrors` stays on; CI runs build + tests on every PR.
