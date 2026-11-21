"""Writes the Parquet files the C# tests read with ParquetNeo, with pyarrow, and a manifest of what is in them.

    python generate.py output_directory

For every file there is an entry in manifest.json: the name, the options it was written with, what the C# test must find in it (the
columns as normalized in normalize.py), and the metadata pyarrow reads back from it (the statistics ParquetNeo must agree with).
"""
import json
import os
import random
import sys
from decimal import Decimal

import pyarrow as pa
import pyarrow.parquet as pq

from dump import describe_metadata
from normalize import normalize_table

MANIFEST = []
SKIPPED = []


def write(directory, name, table, kind="flat", shape=None, **options):
    """Writes a table as <name>.parquet with the options of pyarrow, and records it in the manifest."""
    path = os.path.join(directory, name + ".parquet")
    try:
        pq.write_table(table, path, **options)
    except Exception as error:  # an option this version of pyarrow does not have: the case is left out and said so
        SKIPPED.append({"name": name, "reason": str(error)})
        return
    record(directory, name, table, kind, shape, "pyarrow", options)


def record(directory, name, table, kind, shape, writer, options):
    """Adds a file to the manifest: what is in it (the table it was made from), and what pyarrow reads of its metadata."""
    parquet_file = pq.ParquetFile(os.path.join(directory, name + ".parquet"))
    MANIFEST.append({
        "name": name,
        "file": name + ".parquet",
        "kind": kind,
        "shape": shape,
        "writer": writer,
        "options": {k: v for k, v in options.items() if isinstance(v, (str, int, float, bool, list, dict))},
        "columns": normalize_table(table),
        "column_order": table.column_names,
        "metadata": describe_metadata(parquet_file.metadata),
    })


def basic_table(n):
    def pick(f):
        return [f(i) for i in range(n)]

    def with_nulls(values, every):
        return [None if i % every == 0 else v for i, v in enumerate(values)]

    f32 = pick(lambda i: i * 0.25 - 100.0)
    f64 = pick(lambda i: i * 0.1 - 50.0)
    for position, special in ((5, float("nan")), (6, float("inf")), (7, float("-inf"))):
        f32[position] = special
        f64[position] = special
    text = pick(lambda i: ("x" * 5000) if i % 500 == 499 else "" if i % 17 == 0 else "row %d é€ ü" % i)
    return pa.table({
        "Id": pa.array(pick(lambda i: i), pa.int64()),
        "I8": pa.array(pick(lambda i: ((i * 7) % 256) - 128), pa.int8()),
        "I16": pa.array(pick(lambda i: ((i * 131) % 65536) - 32768), pa.int16()),
        "I32": pa.array(with_nulls(pick(lambda i: i * 997 - 2_000_000), 7), pa.int32()),
        "U8": pa.array(pick(lambda i: (i * 13) % 256), pa.uint8()),
        "U16": pa.array(pick(lambda i: (i * 257) % 65536), pa.uint16()),
        "U32": pa.array(pick(lambda i: (i * 2654435761) % 2**32), pa.uint32()),
        "U64": pa.array(pick(lambda i: (i * 0x9E3779B97F4A7C15) % 2**64), pa.uint64()),
        "F32": pa.array(with_nulls(f32, 11), pa.float32()),
        "F64": pa.array(with_nulls(f64, 13), pa.float64()),
        "B": pa.array(with_nulls(pick(lambda i: i % 3 == 0), 10), pa.bool_()),
        "S": pa.array(with_nulls(text, 13), pa.string()),
        "Bin": pa.array(with_nulls(pick(lambda i: bytes([i % 256]) * (i % 9)), 15), pa.binary()),
    })


def logical_table(n):
    def pick(f):
        return [f(i) for i in range(n)]

    def with_nulls(values, every):
        return [None if i % every == 0 else v for i, v in enumerate(values)]

    def decimals(precision, scale):
        def make(i):
            digits = (i * 7919) % (10 ** min(precision, 15))
            sign = -1 if i % 3 == 0 else 1
            return Decimal(sign * digits).scaleb(-scale)
        return pa.array(with_nulls(pick(make), 9), pa.decimal128(precision, scale))

    columns = {
        "Id": pa.array(pick(lambda i: i), pa.int32()),
        "Date": pa.array(pick(lambda i: i * 3 - 1000), pa.int32()).cast(pa.date32()),
        "Time32": pa.array(pick(lambda i: (i * 86399) % 86_400_000), pa.int32()).cast(pa.time32("ms")),
        "Time64": pa.array(pick(lambda i: (i * 1_234_567) % 86_400_000_000), pa.int64()).cast(pa.time64("us")),
        "TsMillis": pa.array(with_nulls(pick(lambda i: 1_700_000_000_000 + i * 1000), 5), pa.int64()).cast(pa.timestamp("ms")),
        "TsMicrosUtc": pa.array(pick(lambda i: 1_700_000_000_000_000 + i * 1_000_001), pa.int64()).cast(pa.timestamp("us", tz="UTC")),
        "TsNanosUtc": pa.array(pick(lambda i: 1_700_000_000_000_000_000 + i * 100), pa.int64()).cast(pa.timestamp("ns", tz="UTC")),
        "Dec9": decimals(9, 2),
        "Dec18": decimals(18, 4),
        "Dec30": decimals(30, 6),
        "Big": pa.array(with_nulls(pick(lambda i: "large %d" % i), 6), pa.large_string()),
        "BigBin": pa.array(pick(lambda i: bytes([i % 256]) * (i % 5)), pa.large_binary()),
    }
    try:
        columns["Half"] = pa.array(pick(lambda i: float(i % 100) / 4), pa.float32()).cast(pa.float16())
    except Exception as error:  # needs numpy in some versions
        SKIPPED.append({"name": "logical half", "reason": str(error)})
    return pa.table(columns)


def nested_tables(n):
    def list_column(make, value_type):
        return pa.array([make(i) for i in range(n)], pa.list_(value_type))

    tags = list_column(lambda i: None if i % 11 == 0 else [] if i % 7 == 0 else [None if (i + j) % 5 == 0 else "t%d" % ((i + j) % 9) for j in range(i % 4 + 1)], pa.string())
    nums = list_column(lambda i: None if i % 13 == 0 else [i + j if (i + j) % 6 else None for j in range(i % 5)], pa.int32())
    person_type = pa.struct([
        ("Name", pa.string()),
        ("Age", pa.int32()),
        ("Home", pa.struct([("City", pa.string()), ("Zip", pa.int32())])),
    ])

    def person(i):
        if i % 9 == 0:
            return None
        home = None if i % 4 == 0 else {"City": None if i % 10 == 1 else "city %d" % (i % 20), "Zip": i if i % 3 else None}
        return {"Name": "p%d" % i, "Age": None if i % 8 == 0 else 20 + i % 50, "Home": home}

    map_type = pa.map_(pa.string(), pa.int32())

    def mapping(i):
        if i % 10 == 0:
            return None
        return [("k%d" % ((i + j) % 6), None if (i + j) % 7 == 0 else i * j) for j in range(i % 4)]

    item_type = pa.struct([("Sku", pa.string()), ("Qty", pa.int32())])

    def items(i):
        if i % 12 == 0:
            return None
        return [None if (i + j) % 8 == 0 else {"Sku": "s%d" % (i + j), "Qty": None if j % 3 == 2 else i % 7 + j} for j in range(i % 4)]

    ids = pa.array(range(n), pa.int32())
    return {
        "nested_lists": pa.table({"Id": ids, "Tags": tags, "Nums": nums}),
        "nested_struct": pa.table({"Id": ids, "Person": pa.array([person(i) for i in range(n)], person_type)}),
        "nested_map": pa.table({"Id": ids, "Scores": pa.array([mapping(i) for i in range(n)], map_type)}),
        "nested_list_struct": pa.table({"Id": ids, "Items": pa.array([items(i) for i in range(n)], pa.list_(item_type))}),
    }


def strings_table(n):
    rng = random.Random(7)
    words = ["alpha", "alphabet", "alpine", "beta", "betamax", "gamma", "gammaray", "delta"]
    return pa.table({
        "Id": pa.array(range(n), pa.int64()),
        "S": pa.array(["%s-%04d" % (rng.choice(words), i // 3) for i in range(n)], pa.string()),
        "B": pa.array([("%s/%d" % (rng.choice(words), i)).encode() for i in range(n)], pa.binary()),
    })


def main(directory):
    os.makedirs(directory, exist_ok=True)

    # The options: every codec, with and without a dictionary, both versions of the data pages, in several row groups of several pages.
    basic = basic_table(5000)
    index = 0
    for codec in ("none", "snappy", "gzip", "zstd", "lz4_raw"):
        for dictionary in (True, False):
            for version in ("1.0", "2.0"):
                write(directory, "basic_%s_%s_v%s" % (codec, "dict" if dictionary else "plain", version[0]), basic,
                      compression=codec, use_dictionary=dictionary, data_page_version=version,
                      row_group_size=1200, data_page_size=8192,
                      write_page_checksum=index % 2 == 0, write_page_index=index % 3 == 0)
                index += 1

    logical = logical_table(1000)
    write(directory, "logical_types", logical, compression="snappy", row_group_size=400)
    write(directory, "logical_types_decimal_as_integer", logical, compression="zstd", store_decimal_as_integer=True)

    for name, table in nested_tables(300).items():
        shape = name[len("nested_"):]
        write(directory, name + "_v1", table, kind="nested", shape=shape, row_group_size=100)
        write(directory, name + "_v2_zstd", table, kind="nested", shape=shape, data_page_version="2.0", compression="zstd", row_group_size=128, data_page_size=512)

    ints = pa.table({
        "I32": pa.array([(i * 31) % 100000 - 50000 for i in range(4000)], pa.int32()),
        "I64": pa.array([i * 1_000_003 for i in range(4000)], pa.int64()),
    })
    for version in ("1.0", "2.0"):
        write(directory, "encoding_delta_binary_v" + version[0], ints, use_dictionary=False, data_page_version=version,
              column_encoding={"I32": "DELTA_BINARY_PACKED", "I64": "DELTA_BINARY_PACKED"}, row_group_size=1500)
    strings = strings_table(3000)
    write(directory, "encoding_delta_length", strings, use_dictionary=False, data_page_version="2.0",
          column_encoding={"S": "DELTA_LENGTH_BYTE_ARRAY", "B": "DELTA_LENGTH_BYTE_ARRAY"}, row_group_size=1000)
    write(directory, "encoding_delta_byte_array", strings, use_dictionary=False, data_page_version="2.0",
          column_encoding={"S": "DELTA_BYTE_ARRAY", "B": "DELTA_BYTE_ARRAY"}, row_group_size=1000)
    floats = pa.table({
        "F32": pa.array([i * 0.125 - 300 for i in range(3000)], pa.float32()),
        "F64": pa.array([i * 1.0000001 for i in range(3000)], pa.float64()),
    })
    write(directory, "encoding_byte_stream_split", floats, use_dictionary=False, use_byte_stream_split=True, compression="zstd", row_group_size=1000)
    bools = pa.table({"B": pa.array([i % 5 < 2 for i in range(3000)], pa.bool_())})
    write(directory, "encoding_rle_bool", bools, use_dictionary=False, data_page_version="2.0", column_encoding={"B": "RLE"})

    # Features of the file.
    ids = list(range(0, 10000, 2))
    random.Random(3).shuffle(ids)
    bloom = pa.table({"Id": pa.array(ids, pa.int64()), "Name": pa.array(["name %d" % i for i in ids], pa.string())})
    write(directory, "bloom_filters", bloom, kind="bloom", row_group_size=500, compression="snappy",
          bloom_filter_options={"Id": {"ndv": 500, "fpp": 0.01}, "Name": {"ndv": 500, "fpp": 0.01}})

    sorted_ids = pa.table({"Id": pa.array(range(20000), pa.int64()), "Value": pa.array([i * 0.5 for i in range(20000)], pa.float64())})
    write(directory, "page_index", sorted_ids, kind="page_index", row_group_size=10000, data_page_size=2048, write_page_index=True)

    write(directory, "key_value_metadata", pa.table({"Id": pa.array([1, 2, 3], pa.int32())}).replace_schema_metadata({"owner": "tests", "note": "héllo wörld"}))
    write(directory, "empty", pa.table({"Id": pa.array([], pa.int64()), "S": pa.array([], pa.string())}))
    write(directory, "single_row", pa.table({"Id": pa.array([42], pa.int64()), "S": pa.array(["only"], pa.string())}))
    write(directory, "all_nulls", pa.table({"I": pa.array([None] * 500, pa.int32()), "S": pa.array([None] * 500, pa.string())}))

    big_rows = 200_000
    big = pa.table({
        "Id": pa.array(range(big_rows), pa.int64()),
        "Value": pa.array([(i % 1000) / 8 for i in range(big_rows)], pa.float64()),
        "Name": pa.array(["name %d" % (i % 1000) for i in range(big_rows)], pa.string()),
    })
    write(directory, "large", big, compression="zstd", row_group_size=65536)

    fallback = pa.table({"Id": pa.array(range(20000), pa.int32()), "S": pa.array(["value %d" % (i * 7919 % 20011) if i > 5000 else "repeat %d" % (i % 4) for i in range(20000)], pa.string())})
    write(directory, "dictionary_fallback", fallback, dictionary_pagesize_limit=2048, data_page_size=4096)

    versions = {"pyarrow": pa.__version__}
    try:
        import duckdb_cases
    except ImportError as error:  # duckdb is not installed: the files of pyarrow are enough to run the tests that need only them
        SKIPPED.append({"name": "duckdb", "reason": str(error)})
    else:
        versions["duckdb"] = duckdb_cases.generate(directory, record, SKIPPED)

    with open(os.path.join(directory, "manifest.json"), "w", encoding="utf-8") as stream:
        json.dump({"pyarrow": pa.__version__, "versions": versions, "cases": MANIFEST, "skipped": SKIPPED}, stream, allow_nan=False)
    print("wrote %d files with %s; %d skipped" % (len(MANIFEST), ", ".join("%s %s" % kv for kv in versions.items()), len(SKIPPED)))


if __name__ == "__main__":
    main(sys.argv[1])
