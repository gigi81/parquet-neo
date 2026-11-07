"""Writes timestamps as the deprecated INT96 type with pyarrow, to test reading them.

    pip install pyarrow
    python tests/fixtures/generate_int96_fixtures.py

Output goes to tests/ParquetNeo.Tests/Fixtures. The values are mirrored in the test project (Logical/Int96Tests.cs).
"""
import pathlib

import pyarrow as pa
import pyarrow.parquet as pq

OUT = pathlib.Path(__file__).resolve().parent.parent / "ParquetNeo.Tests" / "Fixtures"

ROWS = 40
NANOSECONDS = [0, -1, 1_700_000_000_123_456_789, 4_102_444_800_000_000_001, -2_000_000_000_000_000_000]
MICROSECONDS = [0, -62_135_596_800_000_000, 253_402_300_799_999_999, 1_700_000_000_123_456, -1]  # also years 1 and 9999


def build(optional):
    def values(raw, column):
        return [None if optional and (i + column) % 7 == 0 else raw[i % 5] for i in range(ROWS)]

    return pa.table(
        [pa.array(values(NANOSECONDS, 0), pa.int64()).cast(pa.timestamp("ns")),
         pa.array(values(MICROSECONDS, 1), pa.int64()).cast(pa.timestamp("us"))],
        schema=pa.schema([pa.field("ts_ns", pa.timestamp("ns"), nullable=optional), pa.field("ts_us", pa.timestamp("us"), nullable=optional)]),
    )


for name, optional, options in [
    ("pyarrow-int96-required-plain", False, dict(use_dictionary=False, compression="none")),
    ("pyarrow-int96-optional-plain", True, dict(use_dictionary=False, compression="none")),
    ("pyarrow-int96-optional-dictionary", True, dict(use_dictionary=True)),
    ("pyarrow-int96-optional-v2-snappy", True, dict(use_dictionary=False, data_page_version="2.0", compression="snappy")),
]:
    pq.write_table(build(optional), OUT / f"{name}.parquet", store_schema=False, write_statistics=False,
                   use_deprecated_int96_timestamps=True, **options)
    print("wrote", name)
