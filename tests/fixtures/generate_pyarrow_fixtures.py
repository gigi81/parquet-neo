"""Generates the small pyarrow-written Parquet files used by the unit tests.

    pip install pyarrow
    python tests/fixtures/generate_pyarrow_fixtures.py

Output goes to tests/ParquetNeo.Tests/Fixtures. The generated files are committed, so
running this script is only needed when a new fixture is required.
"""
import datetime as dt
import decimal
import pathlib

import pyarrow as pa
import pyarrow.parquet as pq

OUT = pathlib.Path(__file__).resolve().parent.parent / "ParquetNeo.Tests" / "Fixtures"
OUT.mkdir(parents=True, exist_ok=True)

utc = "UTC"
table = pa.table(
    {
        "id": pa.array([1, 2, 3, 4, 5], pa.int64()),
        "int32": pa.array([10, None, 30, 40, 50], pa.int32()),
        "int8": pa.array([1, 2, 3, 4, 5], pa.int8()),
        "uint16": pa.array([1, 2, 3, 4, 5], pa.uint16()),
        "float": pa.array([1.5, 2.5, None, 4.5, 5.5], pa.float32()),
        "double": pa.array([1.25, 2.25, 3.25, 4.25, 5.25], pa.float64()),
        "flag": pa.array([True, False, True, None, True], pa.bool_()),
        "name": pa.array(["a", "bb", None, "dddd", "eeeee"], pa.string()),
        "payload": pa.array([b"x", b"yy", b"zzz", None, b""], pa.binary()),
        "fixed": pa.array([b"abcd"] * 5, pa.binary(4)),
        "date": pa.array([dt.date(2020, 1, d) for d in range(1, 6)], pa.date32()),
        "time": pa.array([dt.time(1, 2, s) for s in range(5)], pa.time64("us")),
        "ts_ms": pa.array([dt.datetime(2020, 1, 1, 0, 0, s) for s in range(5)], pa.timestamp("ms", tz=utc)),
        "ts_us_local": pa.array([dt.datetime(2020, 1, 1, 0, 0, s) for s in range(5)], pa.timestamp("us")),
        "amount": pa.array([decimal.Decimal(f"{i}.25") for i in range(5)], pa.decimal128(10, 2)),
        "half": pa.array([1.0, 2.0, 3.0, 4.0, 5.0], pa.float16()),
        "tags": pa.array([["a"], [], None, ["b", "c"], ["d"]], pa.list_(pa.string())),
        "point": pa.array(
            [{"x": 1, "y": "p"}, {"x": 2, "y": None}, None, {"x": 4, "y": "q"}, {"x": 5, "y": "r"}],
            pa.struct([("x", pa.int32()), ("y", pa.string())]),
        ),
        "attrs": pa.array(
            [[("k", 1)], [], None, [("a", 2), ("b", 3)], [("z", 9)]], pa.map_(pa.string(), pa.int32())
        ),
    }
)

for name, page_version in (("pyarrow-v1", "1.0"), ("pyarrow-v2", "2.0")):
    pq.write_table(
        table,
        OUT / f"{name}.parquet",
        row_group_size=3,
        compression="snappy",
        data_page_version=page_version,
        write_statistics=True,
        store_schema=False,
    )
    print("wrote", name)
