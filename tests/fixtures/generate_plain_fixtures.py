"""Generates uncompressed, non-dictionary pyarrow files used to test the PLAIN and RLE decoders.

    pip install pyarrow
    python tests/fixtures/generate_plain_fixtures.py

Every value is derived from its row index, so the tests recompute the expected data instead of
embedding it (see PlainFixtureData in the test project). Output goes to tests/ParquetNeo.Tests/Fixtures.
"""
import datetime as dt
import pathlib

import pyarrow as pa
import pyarrow.parquet as pq

OUT = pathlib.Path(__file__).resolve().parent.parent / "ParquetNeo.Tests" / "Fixtures"
OUT.mkdir(parents=True, exist_ok=True)

ROWS = 1000


def nullable(values, is_null):
    return [None if is_null(i) else v for i, v in enumerate(values)]


idx = range(ROWS)
table = pa.table(
    {
        # No nulls: a required-looking column whose definition levels are one long RLE run.
        "dense_int": pa.array([i * 3 - 500 for i in idx], pa.int32()),
        # Nulls on every 7th row: definition levels mix short and long runs.
        "int32": pa.array(nullable([i * 3 - 500 for i in idx], lambda i: i % 7 == 0), pa.int32()),
        # A long null stretch, then values: exercises long RLE runs followed by bit-packed ones.
        "int64": pa.array(nullable([i * 1_000_003 - 7 for i in idx], lambda i: 100 <= i < 600), pa.int64()),
        "float": pa.array(nullable([i * 0.5 - 3.25 for i in idx], lambda i: i % 11 == 0), pa.float32()),
        "double": pa.array(nullable([i * 0.125 + 1e-3 for i in idx], lambda i: i % 13 == 0), pa.float64()),
        "flag": pa.array(nullable([i % 3 == 0 or i % 5 == 0 for i in idx], lambda i: i % 17 == 0), pa.bool_()),
        "flag_rle": pa.array([i % 100 < 70 for i in idx], pa.bool_()),
        "text": pa.array(nullable([f"value-{i}" * (i % 4) for i in idx], lambda i: i % 9 == 0), pa.string()),
        "fixed": pa.array(nullable([bytes([i % 256, 1, 2, 3]) for i in idx], lambda i: i % 5 == 0), pa.binary(4)),
        "ts96": pa.array(
            nullable([dt.datetime(2020, 1, 1) + dt.timedelta(seconds=i * 3601, microseconds=i) for i in idx],
                     lambda i: i % 10 == 0),
            pa.timestamp("ns"),
        ),
    }
)

for name, page_version in (("pyarrow-plain-v1", "1.0"), ("pyarrow-plain-v2", "2.0")):
    pq.write_table(
        table,
        OUT / f"{name}.parquet",
        compression="none",
        use_dictionary=False,
        data_page_version=page_version,
        write_statistics=False,
        use_deprecated_int96_timestamps=True,
        column_encoding={"flag_rle": "RLE"},
        row_group_size=ROWS,
        data_page_size=1 << 20,
        store_schema=False,
    )
    print("wrote", name)
