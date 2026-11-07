"""The dataset for the encoding fixtures, shared by the generator and the checker. The formulas are mirrored in the test
project (tests/ParquetNeo.Tests/Encodings/EncodingDataset.cs), so change both together."""
import pyarrow as pa

ROWS = 3000
# name, arrow type, parquet encoding
COLUMNS = [
    ("delta_int32", pa.int32(), "DELTA_BINARY_PACKED"),
    ("delta_int32_wrap", pa.int32(), "DELTA_BINARY_PACKED"),
    ("delta_int64", pa.int64(), "DELTA_BINARY_PACKED"),
    ("delta_int64_wrap", pa.int64(), "DELTA_BINARY_PACKED"),
    ("delta_constant", pa.int32(), "DELTA_BINARY_PACKED"),
    ("split_float", pa.float32(), "BYTE_STREAM_SPLIT"),
    ("split_double", pa.float64(), "BYTE_STREAM_SPLIT"),
    ("split_int32", pa.int32(), "BYTE_STREAM_SPLIT"),
    ("delta_length", pa.string(), "DELTA_LENGTH_BYTE_ARRAY"),
    ("delta_prefix", pa.string(), "DELTA_BYTE_ARRAY"),
    ("delta_fixed", pa.binary(4), "DELTA_BYTE_ARRAY"),
    ("split_fixed", pa.binary(4), "BYTE_STREAM_SPLIT"),
]


def value(column, i):
    return [
        lambda: i * 3 - 500 + (i % 7) * (i % 11),
        lambda: -(2**31) if i % 2 == 0 else 2**31 - 1,
        lambda: i * 1_000_003 - 7 + (i % 13) * (i % 5),
        lambda: -(2**63) if i % 2 == 0 else 2**63 - 1,
        lambda: 42,
        lambda: i * 0.5 - 3.25,
        lambda: i * 0.125 + 1e-3,
        lambda: i * 7 - 100,
        lambda: "é" * (i % 4) + str(i),
        lambda: f"https://example.com/path/{i // 10}/item{i}",
        lambda: bytes([(i // 256) % 256, i % 256, 1, 2]),
        lambda: bytes([i % 256, 1, 2, 3]),
    ][column]()


def build(optional):
    arrays = []
    for c, (name, arrow_type, _) in enumerate(COLUMNS):
        values = [None if optional and (i + c) % 5 == 0 else value(c, i) for i in range(ROWS)]
        arrays.append(pa.array(values, arrow_type))
    schema = pa.schema([pa.field(name, t, nullable=optional) for name, t, _ in COLUMNS])
    return pa.table(arrays, schema=schema)


def encodings():
    return {name: encoding for name, _, encoding in COLUMNS}
