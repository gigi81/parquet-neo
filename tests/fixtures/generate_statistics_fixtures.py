"""Writes files whose column statistics exercise the awkward cases, with pyarrow.

    pip install pyarrow
    python tests/fixtures/generate_statistics_fixtures.py

Output goes to tests/ParquetNeo.Tests/Fixtures.
"""
import decimal
import math
import pathlib

import pyarrow as pa
import pyarrow.parquet as pq

OUT = pathlib.Path(__file__).resolve().parent.parent / "ParquetNeo.Tests" / "Fixtures"

nan = float("nan")
table = pa.table(
    {
        # NaNs and zeros of both signs
        "f64": pa.array([nan, 0.0, -0.0, 2.5, nan, None, -7.25], pa.float64()),
        "f32": pa.array([1.5, nan, -2.0, None, 9.0, 0.0, 3.0], pa.float32()),
        "all_nan": pa.array([nan, nan, nan, nan, nan, nan, nan], pa.float64()),
        "zeros": pa.array([0.0, -0.0, 0.0, -0.0, 0.0, -0.0, 0.0], pa.float64()),
        # unsigned values above the signed range
        "u32": pa.array([5, 4_000_000_000, 7, None, 0, 2_147_483_648, 9], pa.uint32()),
        "u64": pa.array([5, 2**64 - 1, 7, None, 0, 2**63, 9], pa.uint64()),
        "i64": pa.array([5, -2**63, 7, None, 0, 2**63 - 1, -9], pa.int64()),
        # unsigned byte-wise order: "é" (0xC3 0xA9) sorts after "z"
        "text": pa.array(["banana", "é", "Zed", None, "", "z", "apple"], pa.string()),
        "blob": pa.array([b"\x7f", b"\x80", b"\xff", None, b"", b"\x00", b"\x01"], pa.binary()),
        "fixed": pa.array([b"\x7f\x00", b"\x80\x00", None, b"\x00\x01", b"\xff\xff", b"\x01\x00", b"\x00\x00"], pa.binary(2)),
        # signed order of the represented value
        "money": pa.array(
            [decimal.Decimal("-1.50"), decimal.Decimal("12.25"), None, decimal.Decimal("-300.00"), decimal.Decimal("0.00"),
             decimal.Decimal("99999.99"), decimal.Decimal("-0.01")],
            pa.decimal128(10, 2),
        ),
        "flag": pa.array([True, True, None, True, True, True, True], pa.bool_()),
        "flag_mixed": pa.array([True, False, None, True, False, True, True], pa.bool_()),
        "all_null": pa.array([None] * 7, pa.int32()),
    }
)

for name, dictionary in [("pyarrow-statistics-plain", False), ("pyarrow-statistics-dictionary", True)]:
    pq.write_table(table, OUT / f"{name}.parquet", use_dictionary=dictionary, write_statistics=True,
                   data_page_version="2.0", store_schema=False)
    print("wrote", name)

for c in range(table.num_columns):
    s = pq.ParquetFile(OUT / "pyarrow-statistics-plain.parquet").metadata.row_group(0).column(c).statistics
    print(table.column_names[c], s.has_min_max, s.min if s.has_min_max else None, s.max if s.has_min_max else None, s.null_count)
