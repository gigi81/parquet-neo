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
| 8 | **Nested data** | Repetition levels; Dremel shredding and assembly for structs, lists, maps at the low-level API (including legacy LIST/MAP layouts on read). |
| 9 | **Advanced encodings** | `DELTA_BINARY_PACKED`, `DELTA_LENGTH_BYTE_ARRAY`, `DELTA_BYTE_ARRAY`, `BYTE_STREAM_SPLIT`, data page v2. |
| 10 | **Logical types** | String, enum, JSON, UUID, date, time, timestamp, decimal, sized ints, float16, mapped to .NET types (`DateOnly`, `TimeOnly`, `DateTimeOffset`, `decimal`, `Guid`, `Half`); `ConvertedType` dual-write and fallback; unknown-type fallback; read-only INT96. |

### Milestone 4 – High-level DTO API

| # | Step | Deliverable |
|---|---|---|
| 11 | **High-level write** | Attributes + convention-based schema inference from a DTO; `SerializeAsync<T>` batching rows into row groups; nullable and nested properties. |
| 12 | **High-level read** | `DeserializeAsync<T>` and streaming `IAsyncEnumerable<T>` per row group; column projection driven by the DTO; clear errors for missing/mismatched columns. |

### Milestone 5 – Performance, extras, release

| # | Step | Deliverable |
|---|---|---|
| 13 | **Performance pass** | Benchmarks vs Parquet.Net and ParquetSharp; buffer pooling, parallel column encoding, async I/O tuning driven by results. |
| 14 | **Source generator** | AOT/trimming-safe DTO mapping replacing the reflection path; same tests run against both paths. |
| 15 | **Read-side extras** | Row-group skipping via statistics, page index, bloom filters, CRC32 page checksums, optional `PAR3` / FlatBuffers footer. |
| 16 | **Release** | README with examples, XML docs, NuGet packaging. |
| 17 | **Preview & new types** | ALP encoding, then Variant / Variant shredding and Geospatial (GEOMETRY/GEOGRAPHY). |

Steps 0–7b are the minimum for flat tables, 8 is needed for real-world files, and 11–12
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
