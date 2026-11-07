"""Checks that a file holds the decimal and text dataset, by reading it with pyarrow.

    python tests/fixtures/verify_decimal_text_file.py file.parquet [more files...]   (optional columns unless the name says "required")
"""
import sys

import pyarrow as pa
import pyarrow.parquet as pq

import decimal_text_dataset



def as_strings(column):
    """ENUM reads as binary and JSON as an extension type, which is how pyarrow shows those logical types; both hold UTF-8 text."""
    chunk = column.combine_chunks()
    if hasattr(chunk, "storage"):
        chunk = chunk.storage
    if pa.types.is_binary(chunk.type):
        return [None if v is None else v.decode("utf-8") for v in chunk.to_pylist()]
    return chunk.to_pylist()


status = 0
for path in sys.argv[1:]:
    optional = "required" not in path
    expected = decimal_text_dataset.build(optional)
    table = pq.read_table(path)
    problems = []
    for name in expected.column_names:
        if name in ("enum", "json"):
            if as_strings(table.column(name)) != expected.column(name).to_pylist():
                problems.append(f"{name} differs")
        elif table.schema.field(name).type != expected.schema.field(name).type:
            problems.append(f"{name} has type {table.schema.field(name).type}, expected {expected.schema.field(name).type}")
        elif table.column(name).to_pylist() != expected.column(name).to_pylist():
            problems.append(f"{name} differs")
    print(path, "OK" if not problems else "; ".join(problems))
    status |= bool(problems)
sys.exit(status)
