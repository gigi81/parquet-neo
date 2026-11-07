"""Checks that a file holds the nested sample dataset, by reading it with pyarrow.

    python tests/fixtures/verify_nested_file.py file.parquet [more files...]
"""
import sys

import pyarrow.parquet as pq

import nested_dataset

expected = nested_dataset.build()
status = 0
for path in sys.argv[1:]:
    table = pq.read_table(path)
    problems = []
    if table.num_rows != expected.num_rows:
        problems.append(f"{table.num_rows} rows, expected {expected.num_rows}")
    for name in expected.column_names:
        got = table.column(name).to_pylist()
        want = expected.column(name).to_pylist()
        if got != want:
            first = next(i for i, (a, b) in enumerate(zip(got, want)) if a != b)
            problems.append(f"column {name} differs at row {first}: {got[first]!r} != {want[first]!r}")
    print(path, "OK" if not problems else "; ".join(problems))
    status |= bool(problems)
sys.exit(status)
