"""Reads a Parquet file with pyarrow and prints what is in it as JSON: the data, normalized as in normalize.py, and the metadata.

    python dump.py file.parquet

Page checksums are verified while reading, so a file whose checksums are wrong fails here.
"""
import json
import sys

import pyarrow.parquet as pq

from normalize import normalize_scalar, normalize_table


def bound(statistics, which):
    """A minimum or maximum as a JSON value; null for the kinds the tests do not compare (dates, times, decimals, and the ones pyarrow cannot make a Python value of)."""
    try:
        return normalize_scalar(getattr(statistics, which))
    except (ValueError, OverflowError):
        return None


def describe_metadata(metadata):
    row_groups = []
    for index in range(metadata.num_row_groups):
        group = metadata.row_group(index)
        columns = []
        for position in range(group.num_columns):
            column = group.column(position)
            statistics = None
            if column.is_stats_set and column.statistics is not None:
                s = column.statistics
                statistics = {
                    "has_min_max": s.has_min_max,
                    "min": bound(s, "min") if s.has_min_max else None,
                    "max": bound(s, "max") if s.has_min_max else None,
                    "null_count": s.null_count if s.has_null_count else None,
                }
            columns.append({
                "path": column.path_in_schema,
                "physical_type": column.physical_type,
                "compression": column.compression,
                "encodings": sorted(str(e) for e in column.encodings),
                "has_dictionary_page": column.has_dictionary_page,
                "has_column_index": column.has_column_index,
                "has_offset_index": column.has_offset_index,
                "has_bloom_filter": column.bloom_filter_offset is not None,
                "num_values": column.num_values,
                "statistics": statistics,
            })
        row_groups.append({"num_rows": group.num_rows, "columns": columns})
    key_value = {}
    if metadata.metadata:
        key_value = {k.decode(): v.decode() for k, v in metadata.metadata.items() if not k.startswith(b"ARROW:")}
    return {
        "num_rows": metadata.num_rows,
        "num_row_groups": metadata.num_row_groups,
        "created_by": metadata.created_by,
        "key_value_metadata": key_value,
        "row_groups": row_groups,
    }


def main(path):
    parquet_file = pq.ParquetFile(path, page_checksum_verification=True)
    table = parquet_file.read()
    result = {
        "metadata": describe_metadata(parquet_file.metadata),
        "schema": [{"name": f.name, "type": str(f.type), "nullable": f.nullable} for f in table.schema],
        "columns": normalize_table(table),
    }
    json.dump(result, sys.stdout, allow_nan=False)


if __name__ == "__main__":
    main(sys.argv[1])
