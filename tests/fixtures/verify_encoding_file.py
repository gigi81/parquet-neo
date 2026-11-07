"""Checks that a file holds the encoding dataset, by reading it with pyarrow.

    python tests/fixtures/verify_encoding_file.py file.parquet [more files...]   (optional columns: the file name says "optional" or "golden")
"""
import sys

import pyarrow.parquet as pq

import encoding_dataset

status = 0
for path in sys.argv[1:]:
    optional = "optional" in path or "golden" in path
    expected = encoding_dataset.build(optional)
    table = pq.read_table(path)
    problems = []
    for name in expected.column_names:
        got = table.column(name).to_pylist()
        want = expected.column(name).to_pylist()
        if got != want:
            first = next(i for i, (a, b) in enumerate(zip(got, want)) if a != b)
            problems.append(f"column {name} differs at row {first}: {got[first]!r} != {want[first]!r}")
    encodings = {pq.ParquetFile(path).metadata.row_group(0).column(c).path_in_schema: pq.ParquetFile(path).metadata.row_group(0).column(c).encodings for c in range(len(expected.column_names))}
    print(path, "OK" if not problems else "; ".join(problems))
    status |= bool(problems)
sys.exit(status)
