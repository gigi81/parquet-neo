"""Checks that a file written by ParquetSerializer holds the Sample rows (tests/ParquetNeo.Tests/Serialization/Sample.cs), by
reading it with pyarrow. The expected values are the same formulas of the row number as in Sample.Row, and the types are the
ones the serializer's convention gives.

    python tests/fixtures/verify_serializer_file.py file.parquet [more files...]
"""
import datetime as dt
import sys
from decimal import Decimal

import struct
import pyarrow as pa
import pyarrow.parquet as pq

ROWS = 40
EPOCH = dt.datetime(1970, 1, 1)


def guid(a, b, c, rest):
    return a.to_bytes(4, "big") + b.to_bytes(2, "big") + c.to_bytes(2, "big") + bytes(rest)


def utc(value):
    return value.replace(tzinfo=dt.timezone.utc)


def rows(i):
    return {
        "Flag": i % 2 == 0,
        "Tiny": i % 200 - 100,
        "Byte": i * 5 % 256,
        "Short": i * 1000 % 60000 - 30000,
        "UShort": i * 1500 % 65536,
        "Int": i * 100_000 - 2_000_000,
        "UInt": i * 100_000_000 % 4_000_000_000,
        "Long": i * 1_000_000_007 - 20_000_000_000,
        "ULong": i * 400_000_000_000_000_000 % 16_000_000_000_000_000_000,
        "Float": i * 0.5 - 10,
        "Double": i * 0.25 - 5,
        "Half": struct.unpack("<H", struct.pack("<e", i * 0.5))[0],
        "Money": Decimal(i) * Decimal("12345.67") - Decimal(100000),
        "Precise": Decimal(i) * Decimal("0.125"),
        "Name": f"name {i}",
        "Data": bytes([i, i + 1]),
        "Id": guid(i, 0, 0, [1, 2, 3, 4, 5, 6, 7, 8]),
        "When": utc(EPOCH + dt.timedelta(days=i, seconds=i, microseconds=i)),
        "Offset": utc(EPOCH + dt.timedelta(hours=i, microseconds=i)),
        "Day": dt.date(2000, 1, 1) + dt.timedelta(days=i * 37),
        "Time": (dt.datetime.combine(dt.date(2000, 1, 1), dt.time()) + dt.timedelta(microseconds=i * 1_234_567)).time(),
        "Nickname": None if i % 3 == 0 else f"nick{i}",
        "OptionalInt": None if i % 4 == 1 else i,
        "OptionalDouble": None if i % 5 == 2 else i / 4.0,
        "OptionalMoney": None if i % 6 == 3 else Decimal(i) * Decimal("1.5"),
        "OptionalWhen": None if i % 7 == 4 else utc(EPOCH + dt.timedelta(minutes=i)),
        "OptionalId": None if i % 8 == 5 else guid(i, 1, 2, [3, 4, 5, 6, 7, 8, 9, 10]),
    }


TYPES = {
    "Flag": pa.bool_(), "Tiny": pa.int8(), "Byte": pa.uint8(), "Short": pa.int16(), "UShort": pa.uint16(), "Int": pa.int32(),
    "UInt": pa.uint32(), "Long": pa.int64(), "ULong": pa.uint64(), "Float": pa.float32(), "Double": pa.float64(),
    "Half": pa.float16(), "Money": pa.decimal128(12, 2), "Precise": pa.decimal128(38, 18), "Name": pa.string(), "Data": pa.binary(),
    "When": pa.timestamp("us", tz="UTC"), "Offset": pa.timestamp("us", tz="UTC"), "Day": pa.date32(), "Time": pa.time64("us"),
    "Nickname": pa.string(), "OptionalInt": pa.int32(), "OptionalDouble": pa.float64(), "OptionalMoney": pa.decimal128(12, 2),
    "OptionalWhen": pa.timestamp("us", tz="UTC"),
}
REQUIRED = {"Flag", "Tiny", "Byte", "Short", "UShort", "Int", "UInt", "Long", "ULong", "Float", "Double", "Half", "Money", "Precise",
            "Name", "Data", "Id", "When", "Offset", "Day", "Time"}


def values(column):
    chunk = column.combine_chunks()
    if hasattr(chunk, "storage"):
        chunk = chunk.storage
    if pa.types.is_float16(chunk.type):
        return chunk.view(pa.uint16()).to_pylist()
    return chunk.to_pylist()


status = 0
for path in sys.argv[1:]:
    table = pq.read_table(path)
    parquet = pq.ParquetFile(path)
    expected = [rows(i) for i in range(ROWS)]
    problems = []
    if table.num_rows != ROWS:
        problems.append(f"{table.num_rows} rows")
    for index, name in enumerate(table.column_names):
        field = table.schema.field(name)
        if name in TYPES and field.type != TYPES[name]:
            problems.append(f"{name} has type {field.type}, expected {TYPES[name]}")
        if field.nullable == (name in REQUIRED):
            problems.append(f"{name} nullable={field.nullable}")
        if name in ("Id", "OptionalId") and parquet.schema.column(index).logical_type.type != "UUID":
            problems.append(f"{name} is not a UUID column")
        if values(table.column(name)) != [row[name] for row in expected]:
            problems.append(f"{name} differs")
    print(path, "OK" if not problems else "; ".join(problems))
    status |= bool(problems)
sys.exit(status)
