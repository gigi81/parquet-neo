"""Checks that a file holds the logical type dataset and the logical types, by reading it with pyarrow.

    python tests/fixtures/verify_logical_file.py file.parquet [more files...]   (optional columns unless the name says "required")
"""
import sys

import pyarrow as pa
import pyarrow.parquet as pq

import logical_dataset


def stored(column):
    """The stored integers or bytes of a column, whatever its logical type."""
    chunk = column.combine_chunks()
    if hasattr(chunk, "storage"):
        chunk = chunk.storage
    if pa.types.is_float16(chunk.type):
        return chunk.view(pa.uint16()).to_pylist()
    if pa.types.is_fixed_size_binary(chunk.type):
        return chunk.to_pylist()
    if pa.types.is_integer(chunk.type):
        return chunk.to_pylist()
    return chunk.cast(pa.int32() if chunk.type in (pa.date32(), pa.time32("ms")) else pa.int64()).to_pylist()


status = 0
for path in sys.argv[1:]:
    optional = "required" not in path
    expected = logical_dataset.build(optional)
    table = pq.read_table(path)
    parquet_schema = pq.ParquetFile(path).schema
    problems = []
    for index, name in enumerate(expected.column_names):
        if name == "uuid":
            # Read back without the Arrow schema, a UUID column shows as fixed_size_binary; its Parquet logical type tells.
            if parquet_schema.column(index).logical_type.type != "UUID":
                problems.append("uuid is not a UUID column")
            if stored(table.column(name)) != stored(expected.column(name)):
                problems.append("uuid differs")
        elif table.schema.field(name).type != expected.schema.field(name).type:
            problems.append(f"{name} has type {table.schema.field(name).type}, expected {expected.schema.field(name).type}")
        else:
            want = stored(expected.column(name))
            if "golden" in path and name in ("time_ns", "timestamp_ns"):
                # The golden file was written from .NET values, which count 100 ns ticks: finer precision is rounded down.
                want = [None if v is None else v // 100 * 100 for v in want]
            if stored(table.column(name)) != want:
                problems.append(f"{name} differs")
    print(path, "OK" if not problems else "; ".join(problems))
    status |= bool(problems)
sys.exit(status)
