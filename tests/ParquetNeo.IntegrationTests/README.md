# Integration tests against pyarrow

ParquetNeo reads and writes files that other implementations read and write. These tests check that with **pyarrow**, the Python bindings of
Apache Arrow's C++ Parquet implementation, which is the reference most tools agree with.

- **Python → ParquetNeo.** `python/generate.py` writes about 45 files with pyarrow (every codec, with and without a dictionary, data pages
  V1 and V2, page index, page checksums, bloom filters, the delta and byte-stream-split encodings, dates, times, timestamps, decimals, half floats,
  lists, structs and maps with nulls at every level, empty files, 200 000 rows) and a `manifest.json` of what is in them. `PythonToParquetNeoTests`
  reads each with ParquetNeo and compares every value, the statistics, the row groups, the key-value metadata, bloom filters and page indexes.
- **ParquetNeo → Python.** `ParquetNeoToPythonTests` writes files (every codec and page layout, logical types, nested rows, encodings, bloom
  filters, parallel and asynchronous writers, ADO.NET, edge cases such as NaN, empty, huge and non-ASCII values) and `python/dump.py` reads them
  with pyarrow, which also verifies the page checksums. The test compares the values and the metadata with what was written.

Both sides use the same JSON rules for values (`python/normalize.py` and `Values.cs`): dates, times and timestamps are the integer count of
the unit of the column, decimals are text with their scale, binary values are hex, and so on.

## Running them

The tests are skipped unless `PARQUETNEO_PYTHON_TESTS=1`, so that `dotnet test` of the solution needs no Python. In the workflow
(`.github/workflows/integration-tests.yml`) the variable is set, so a missing Python or pyarrow fails.

```bash
python3 -m venv .venv && .venv/bin/pip install -r tests/ParquetNeo.IntegrationTests/python/requirements.txt
export PARQUETNEO_PYTHON_TESTS=1 PARQUETNEO_PYTHON="$PWD/.venv/bin/python"
dotnet test --project tests/ParquetNeo.IntegrationTests -c Release
```

`PARQUETNEO_PYTHON` names the interpreter (default `python3`, or `python` on Windows). pyarrow is pinned in `python/requirements.txt`, so that a
release of pyarrow does not make a push fail; raise it on purpose.

## Against the latest pyarrow

The workflow runs on pushes to `main` with the pinned pyarrow, and **every Monday with the latest release** (`pip install --upgrade pyarrow`), so that a new
pyarrow, or a change in how it reads or writes Parquet, shows up as a failed scheduled run and not as a surprise. When it fails, the log says which file
and which value differ; if the difference is in pyarrow and is as it should be, change the test, and when the run passes with the new release, raise the pin.

To try a version by hand: Actions, Integration tests, Run workflow, and give `latest`, `pinned` or a version such as `27.0.0`. Locally,
`pip install --upgrade pyarrow` in the virtual environment above does the same. The summary of every run says which pyarrow it used.

## Adding a case

- A file for ParquetNeo to read: add a `write(...)` call in `generate.py`. A `flat` case (the default) is compared column by column with
  `ParquetDataReader`; a `nested` case needs a class in `Rows.cs` and a line in `PythonToParquetNeoTests.TheRowsOfANestedFile_AreThoseOfPython`.
- A file for pyarrow to read: write it with ParquetNeo in `ParquetNeoToPythonTests` and compare with `Checks.CompareFlat` or `Checks.CompareNested`.

## What these tests cannot see

pyarrow does not read bloom filters or the page index to answer a question, so for files that ParquetNeo wrote these tests check that they are
there (and that the file is valid, and the checksums are right), not that their contents are exact. The bloom filters and page indexes pyarrow wrote
are used by ParquetNeo, and that is checked. `LZ4_RAW` and `LZ4` have the same name in pyarrow's metadata: that pyarrow decodes the pages is what
shows that ParquetNeo wrote the raw one.
