# Packages

Every package has the version of the release, and a package that depends on another asks for the same version.

| Package | Contents | AOT | Depends on |
|---|---|---|---|
| `ParquetNeo.All` | Nothing of its own: it installs all the packages below | – | all of them |
| `ParquetNeo` | The low-level reader and writer, the schema, the encodings, Gzip and Brotli, the ADO.NET reader and writer, filters, the page index and bloom filters | yes | `ParquetNeo.Enums` |
| `ParquetNeo.Enums` | The enumerations of the format and of the options (in the `ParquetNeo` namespace) | yes | – |
| `ParquetNeo.Serialization` | `ParquetSerializer`, the attributes of the mapping, and the source generator for `[ParquetSerializable]` | yes, with the generator | `ParquetNeo` |
| `ParquetNeo.Linq` | `ParquetDataset<T>`: an `IQueryable<T>` over a file or a directory | no | `ParquetNeo.Serialization` |
| `ParquetNeo.Compression.Snappy` | Snappy, through the managed Snappier library | yes | `ParquetNeo` |
| `ParquetNeo.Compression.Lz4` | LZ4_RAW | yes | `ParquetNeo` |
| `ParquetNeo.Compression.Zstd` | Zstandard, through the managed ZstdSharp library | yes | `ParquetNeo` |

The source generator is not a package of its own: it is inside `ParquetNeo.Serialization`, and runs in every project that
references it, directly or through `ParquetNeo.Linq` or `ParquetNeo.All`.

All the types are in the `ParquetNeo` namespace or one below it, whatever the package they are in.

## Frameworks

Every package has a build for `net10.0` and one for `net11.0`: .NET 10 is the long term support release and .NET 11 the current one.
The tests run on both, and the Native AOT checks publish for both. The source generator runs in the compiler and works with
the SDK of either.

## Versions

The version comes from the git tag of the release: `v1.2.3` is version 1.2.3 and `v1.2.3-preview.1` is a prerelease.
