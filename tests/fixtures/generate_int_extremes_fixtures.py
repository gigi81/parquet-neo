"""Generates small pyarrow files holding the extreme values of every integer type.

    pip install pyarrow
    python tests/fixtures/generate_int_extremes_fixtures.py

All columns are required (no definition levels) and uncompressed, so the tests can decode the raw
page bytes. Both PLAIN and dictionary-encoded variants are written, as data page v1 and v2, with
min/max statistics. Output goes to tests/ParquetNeo.Tests/Fixtures.
"""
import pathlib

import pyarrow as pa
import pyarrow.parquet as pq

OUT = pathlib.Path(__file__).resolve().parent.parent / "ParquetNeo.Tests" / "Fixtures"
OUT.mkdir(parents=True, exist_ok=True)


def extremes(bits, signed):
    if signed:
        return [-(1 << (bits - 1)), -1, 0, 1, (1 << (bits - 1)) - 1]
    return [0, 1, (1 << (bits - 1)) - 1, 1 << (bits - 1), (1 << bits) - 1]


types = {
    "int8": (pa.int8(), 8, True),
    "int16": (pa.int16(), 16, True),
    "int32": (pa.int32(), 32, True),
    "int64": (pa.int64(), 64, True),
    "uint8": (pa.uint8(), 8, False),
    "uint16": (pa.uint16(), 16, False),
    "uint32": (pa.uint32(), 32, False),
    "uint64": (pa.uint64(), 64, False),
}

schema = pa.schema([pa.field(name, t, nullable=False) for name, (t, _, _) in types.items()])
table = pa.table(
    {name: pa.array(extremes(bits, signed), t) for name, (t, bits, signed) in types.items()},
    schema=schema,
)

for encoding, use_dictionary in (("plain", False), ("dict", True)):
    for page_version in ("1.0", "2.0"):
        name = f"pyarrow-int-extremes-{encoding}-v{page_version[0]}"
        pq.write_table(
            table,
            OUT / f"{name}.parquet",
            compression="none",
            use_dictionary=use_dictionary,
            data_page_version=page_version,
            write_statistics=True,
            store_schema=False,
        )
        print("wrote", name)
