"""Writes the 13-column sample dataset (see SampleFile in the test project) with pyarrow.

    pip install pyarrow
    python tests/fixtures/generate_flat_fixtures.py

The values come from the same formulas as SampleFile.Value, and in the "optional" files row r of column c is
null when (r + c) % 5 == 0. Dictionary encoding is off unless the file name says dict. Output goes to
tests/ParquetNeo.Tests/Fixtures.
"""
import datetime as dt
import decimal
import pathlib

import pyarrow as pa
import pyarrow.parquet as pq

OUT = pathlib.Path(__file__).resolve().parent.parent / "ParquetNeo.Tests" / "Fixtures"
OUT.mkdir(parents=True, exist_ok=True)

NAMES = ["i32", "i64", "f32", "f64", "flag", "text", "blob", "fixed", "day", "ts", "money", "small", "big"]
TYPES = [
    pa.int32(), pa.int64(), pa.float32(), pa.float64(), pa.bool_(), pa.string(), pa.binary(), pa.binary(4),
    pa.date32(), pa.timestamp("us", tz="UTC"), pa.decimal128(10, 2), pa.int8(), pa.uint64(),
]


def value(column, i):
    return [
        lambda: i * 3 - 500,
        lambda: i * 1_000_003 - 7,
        lambda: i * 0.5 - 3.25,
        lambda: i * 0.125 + 1e-3,
        lambda: i % 3 == 0,
        lambda: "é" * (i % 4) + str(i),
        lambda: bytes([i % 256]) * (i % 5),
        lambda: bytes([i % 256, 1, 2, 3]),
        lambda: dt.date(1970, 1, 1) + dt.timedelta(days=18000 + i),
        lambda: dt.datetime(1970, 1, 1, tzinfo=dt.timezone.utc)
        + dt.timedelta(microseconds=1_600_000_000_000_000 + i * 1_000_000),
        lambda: decimal.Decimal(i * 100 + 25).scaleb(-2),
        lambda: i % 200 - 100,
        lambda: (i * 4_000_000_000_000_000) % 2**64,
    ][column]()


def table(rows, optional):
    arrays = []
    for c, name in enumerate(NAMES):
        values = [None if optional and (i + c) % 5 == 0 else value(c, i) for i in range(rows)]
        arrays.append(pa.array(values, TYPES[c]))
    schema = pa.schema([pa.field(n, t, nullable=optional) for n, t in zip(NAMES, TYPES)])
    return pa.table(arrays, schema=schema)


def write(name, rows, optional, page_version, compression="none", dictionary=False, statistics=False, **options):
    pq.write_table(
        table(rows, optional),
        OUT / f"{name}.parquet",
        compression=compression,
        use_dictionary=dictionary,
        data_page_version=page_version,
        write_statistics=statistics,
        store_schema=False,
        **options,
    )
    print("wrote", name)


write("pyarrow-flat-required-v1", 1000, False, "1.0")
write("pyarrow-flat-optional-v1", 1000, True, "1.0")
write("pyarrow-flat-optional-v2", 1000, True, "2.0")
write("pyarrow-flat-required-v2-snappy", 1000, False, "2.0", compression="snappy")
write("pyarrow-flat-optional-v1-smallpages", 3000, True, "1.0", compression="snappy", data_page_size=1500, write_batch_size=64)

# Dictionary encoded: every column of the sample dataset except the booleans, which pyarrow never dictionary encodes.
write("pyarrow-flat-required-dict-v1", 1000, False, "1.0", dictionary=True)
write("pyarrow-flat-optional-dict-v1", 1000, True, "1.0", dictionary=True)
write("pyarrow-flat-optional-dict-v2", 1000, True, "2.0", dictionary=True)
write("pyarrow-flat-optional-dict-v1-snappy-smallpages", 3000, True, "1.0", compression="snappy", dictionary=True, data_page_size=1500, write_batch_size=64)
# A dictionary that outgrows its limit: pyarrow starts with dictionary pages and falls back to PLAIN pages.
write("pyarrow-flat-required-dict-fallback", 3000, False, "1.0", dictionary=True, dictionary_pagesize_limit=2000, data_page_size=1500, write_batch_size=64)
# Format version 1.0 writers (and the Parquet 1.x era in general) use the deprecated PLAIN_DICTIONARY for dictionary
# pages and for the data pages that use them.
write("pyarrow-flat-optional-dict-legacy", 1000, True, "1.0", dictionary=True, version="1.0")
# With statistics, over several row groups (400 rows each; the last one has 200).
write("pyarrow-flat-optional-statistics", 1000, True, "1.0", statistics=True, row_group_size=400)
write("pyarrow-flat-required-statistics-dictionary", 1000, False, "2.0", dictionary=True, statistics=True, row_group_size=400)
