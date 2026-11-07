"""The dataset for the logical type fixtures, shared by the generator and the checker. The values are mirrored in the test
project (tests/ParquetNeo.Tests/Logical/LogicalDataset.cs), so change both together. Every column repeats five values."""
import uuid

import pyarrow as pa

ROWS = 40
# name, arrow type, five raw values (integers are the stored counts; uuid and float16 are bytes)
COLUMNS = [
    ("int8", pa.int8(), [-128, -1, 0, 1, 127]),
    ("int16", pa.int16(), [-32768, -1, 0, 1, 32767]),
    ("uint8", pa.uint8(), [0, 1, 127, 128, 255]),
    ("uint16", pa.uint16(), [0, 1, 32767, 32768, 65535]),
    ("uint32", pa.uint32(), [0, 1, 2147483647, 2147483648, 4294967295]),
    ("uint64", pa.uint64(), [0, 1, 2**63 - 1, 2**63, 2**64 - 1]),
    ("date", pa.date32(), [0, -719162, 2932896, -1, 19782]),  # 1970-01-01, 0001-01-01, 9999-12-31, 1969-12-31, 2024-02-29
    ("time_ms", pa.time32("ms"), [0, 1, 86_399_999, 43_200_000, 12_345]),
    ("time_us", pa.time64("us"), [0, 1, 86_399_999_999, 43_200_000_000, 123_456_789]),
    ("time_ns", pa.time64("ns"), [0, 100, 86_399_999_999_999, 43_200_000_000_000, 123_456_789_012]),
    ("timestamp_ms", pa.timestamp("ms", tz="UTC"), [0, -1, 1, 1_700_000_000_123, -62_135_596_800_000]),
    ("timestamp_us", pa.timestamp("us", tz="UTC"), [0, -1, 1, 1_700_000_000_123_456, 253_402_300_799_999_999]),
    ("timestamp_ns", pa.timestamp("ns", tz="UTC"), [0, -1, 100, 1_700_000_000_123_456_700, -5_000_000_000_000_000_000]),
    ("local_us", pa.timestamp("us"), [0, -1, 1, 1_700_000_000_123_456, -62_135_596_800_000_000]),
    ("uuid", pa.uuid(), [uuid.UUID(h).bytes for h in [
        "00000000-0000-0000-0000-000000000000", "ffffffff-ffff-ffff-ffff-ffffffffffff", "00112233-4455-6677-8899-aabbccddeeff",
        "123e4567-e89b-12d3-a456-426614174000", "f81d4fae-7dec-11d0-a765-00a0c91e6bf6"]]),
    ("half", pa.float16(), [0x0000, 0x8000, 0x3C00, 0x7BFF, 0xFC00]),  # 0, -0, 1, 65504, -infinity
]


def _array(arrow_type, raw, optional, column):
    values = [None if optional and (i + column) % 7 == 0 else raw[i % 5] for i in range(ROWS)]
    if arrow_type == pa.uuid():
        return pa.ExtensionArray.from_storage(arrow_type, pa.array(values, pa.binary(16)))
    if arrow_type == pa.float16():
        return pa.array(values, pa.uint16()).view(arrow_type)
    if pa.types.is_integer(arrow_type):
        return pa.array(values, arrow_type)
    # dates, times and timestamps are built from their stored integers
    storage = pa.int32() if arrow_type in (pa.date32(), pa.time32("ms")) else pa.int64()
    return pa.array(values, storage).cast(arrow_type)


def build(optional):
    arrays = [_array(t, raw, optional, c) for c, (_, t, raw) in enumerate(COLUMNS)]
    schema = pa.schema([pa.field(name, array.type, nullable=optional) for (name, _, _), array in zip(COLUMNS, arrays)])
    return pa.table(arrays, schema=schema)
