"""How values are written to JSON, so that the C# tests can compare what pyarrow holds with what ParquetNeo holds.

Both sides use the same rules:
  * integers, booleans and strings are themselves;
  * floats are numbers, except NaN, Infinity and -Infinity, which are those strings;
  * decimals are strings with the scale of the column ("1.50");
  * binary values are lower case hex strings (a UUID is the hex of its 16 bytes);
  * dates, times and timestamps are the integer count of the unit of the column (days; milliseconds, microseconds or
    nanoseconds) since the epoch, with no time zone applied;
  * a list is a list, a struct is an object, and a map with text keys is an object.
"""
import math

import pyarrow as pa


def _float(value):
    if value is None:
        return None
    value = float(value)
    if math.isnan(value):
        return "NaN"
    if math.isinf(value):
        return "Infinity" if value > 0 else "-Infinity"
    return value


def _hex(value):
    return None if value is None else bytes(value).hex()


def normalize_array(array):
    """The values of an array (or chunked array) as a list of JSON values."""
    if isinstance(array, pa.ChunkedArray):
        array = array.combine_chunks() if array.num_chunks != 1 else array.chunk(0)
    t = array.type
    if isinstance(t, pa.BaseExtensionType):
        array = array.storage
        t = array.type
    if pa.types.is_timestamp(t) or pa.types.is_time64(t) or pa.types.is_duration(t):
        return array.cast(pa.int64()).to_pylist()
    if pa.types.is_date32(t) or pa.types.is_time32(t):
        return array.cast(pa.int32()).to_pylist()
    if pa.types.is_decimal(t):
        return [None if v is None else format(v, "f") for v in array.to_pylist()]
    if pa.types.is_floating(t):
        return [_float(v) for v in array.to_pylist()]
    if pa.types.is_binary(t) or pa.types.is_large_binary(t) or pa.types.is_fixed_size_binary(t):
        return [_hex(v) for v in array.to_pylist()]
    if pa.types.is_list(t) or pa.types.is_large_list(t) or pa.types.is_fixed_size_list(t):
        return [None if v is None else normalize_array(pa.array(v, t.value_type)) for v in array.to_pylist()]
    if pa.types.is_struct(t):
        names = [t.field(i).name for i in range(t.num_fields)]
        columns = [normalize_array(array.field(i)) for i in range(t.num_fields)]
        validity = array.is_valid().to_pylist()
        return [dict(zip(names, (c[i] for c in columns))) if validity[i] else None for i in range(len(array))]
    if pa.types.is_map(t):
        result = []
        for v in array.to_pylist():
            if v is None:
                result.append(None)
                continue
            keys = normalize_array(pa.array([k for k, _ in v], t.key_type))
            items = normalize_array(pa.array([x for _, x in v], t.item_type))
            result.append(dict(zip(keys, items)))
        return result
    return array.to_pylist()


def normalize_table(table):
    return {name: normalize_array(table.column(name)) for name in table.column_names}


def normalize_scalar(value):
    """A statistic (a minimum, a maximum) as a JSON value; None for the kinds the tests do not compare."""
    if value is None or isinstance(value, bool):
        return value
    if isinstance(value, int):
        return value
    if isinstance(value, float):
        return _float(value)
    if isinstance(value, str):
        return value
    if isinstance(value, (bytes, bytearray)):
        return bytes(value).hex()
    return None
