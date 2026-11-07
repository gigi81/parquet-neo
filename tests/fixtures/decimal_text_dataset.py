"""The dataset for the decimal and text fixtures, shared by the generator and the checker. The values are mirrored in the test
project (tests/ParquetNeo.Tests/Logical/DecimalTextDataset.cs), so change both together. Every column repeats five values."""
from decimal import Decimal

import pyarrow as pa

ROWS = 40
# name, arrow type, five values. pyarrow stores decimals of up to 9 digits as INT32 and up to 18 as INT64 when asked to,
# and as FIXED_LEN_BYTE_ARRAY otherwise; "decimal_bytes" is stored as BYTE_ARRAY by our writer only.
COLUMNS = [
    ("decimal_int32", pa.decimal128(9, 2), ["0.00", "-0.01", "1234567.89", "-9999999.99", "42.50"]),
    ("decimal_int64", pa.decimal128(18, 4), ["0.0000", "-0.0001", "12345678901234.5678", "-99999999999999.9999", "3.1416"]),
    ("decimal_fixed", pa.decimal128(28, 6), ["0.000000", "-0.000001", "1234567890123456789012.345678", "-9999999999999999999999.999999", "2.718282"]),
    ("decimal_wide", pa.decimal256(40, 10), ["0.0000000000", "-1.5000000000", "123456789012345678.1234567890", "-123456789012345678.0000000001", "0.0000000007"]),
    ("decimal_bytes", pa.decimal128(20, 3), ["0.000", "-0.001", "99999999999999999.999", "-99999999999999999.999", "1000.500"]),
    ("text", pa.string(), ["", "a", "héllo wörld", "日本語", "\U0001F600 emoji"]),
    ("enum", pa.string(), ["RED", "GREEN", "BLUE", "RED", "GREEN"]),
    ("json", pa.string(), ['{"a":1}', "null", "[]", '"x"', '{"é":"日本"}']),
]


def _array(arrow_type, raw, optional, column):
    values = [None if optional and (i + column) % 7 == 0 else raw[i % 5] for i in range(ROWS)]
    if pa.types.is_decimal(arrow_type):
        values = [None if v is None else Decimal(v) for v in values]
    return pa.array(values, arrow_type)


def build(optional):
    arrays = [_array(t, raw, optional, c) for c, (_, t, raw) in enumerate(COLUMNS)]
    schema = pa.schema([pa.field(name, array.type, nullable=optional) for (name, _, _), array in zip(COLUMNS, arrays)])
    return pa.table(arrays, schema=schema)
