# ParquetNeo

A high-performance .NET library for reading and writing [Apache Parquet](https://parquet.apache.org/) files.

> **Status:** early development. Nothing is usable yet — see the [implementation plan](docs/PLAN.md).

## Goals

- **Fast:** span-based, allocation-conscious, SIMD where it pays off.
- **Two layers:**
  - a *low-level* API working on row groups and column chunks,
  - a *high-level* API that reads and writes plain DTOs.
- **Minimal dependencies:** the core depends only on the BCL.

## Repository layout

| Path | Contents |
|---|---|
| `src/ParquetNeo` | The library |
| `tests/ParquetNeo.Tests` | Unit tests ([TUnit](https://tunit.dev/)) |
| `benchmarks/ParquetNeo.Benchmarks` | [BenchmarkDotNet](https://benchmarkdotnet.org/) benchmarks |
| `specs` | Local copy of the Parquet format specification and `parquet.thrift` |
| `docs` | Project documentation, including the plan |

## Building and testing

Requires the .NET 10 SDK.

```bash
dotnet build -c Release
```

```bash
dotnet test -c Release
```

## License

MIT — see [LICENSE.md](LICENSE.md).
