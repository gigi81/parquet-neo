"""Asks DuckDB what it sees in the bloom filters and the page index of a file, which only it and a few others read.

    python duckdb_probe.py request.json

The request is {"file": "...", "bloom": [{"column": "Id", "type": "BIGINT", "values": [...]}], "page_index": true}. For each value of a column
DuckDB says, for every row group, whether the bloom filter rules the value out (parquet_bloom_probe); the page index is what parquet_page_index reads.
A value is a number, a string, or for BLOB the hex of its bytes. The answer is JSON.
"""
import json
import sys

import duckdb


def literal(value, kind):
    """The SQL of a value of a DuckDB type: the function takes a constant."""
    if kind == "BLOB":
        return "unhex('%s')" % value
    if kind == "VARCHAR":
        return "'%s'" % str(value).replace("'", "''")
    if kind in ("FLOAT", "DOUBLE"):
        return "%r::%s" % (float(value), kind)
    return "%d::%s" % (int(value), kind)


def main(request_path):
    with open(request_path, encoding="utf-8") as stream:
        request = json.load(stream)
    connection = duckdb.connect()
    path = request["file"]
    answer = {"duckdb": duckdb.__version__}

    answer["bloom"] = []
    for probe in request.get("bloom", []):
        results = {}
        for value in probe["values"]:
            rows = connection.execute(
                "SELECT row_group_id, bloom_filter_excludes FROM parquet_bloom_probe(?, ?, %s) ORDER BY row_group_id" % literal(value, probe["type"]),
                [path, probe["column"]]).fetchall()
            results[str(value)] = [bool(excludes) for _, excludes in rows]
        answer["bloom"].append({"column": probe["column"], "results": results})

    if request.get("page_index"):
        rows = connection.execute("SELECT * FROM parquet_page_index(?) ORDER BY row_group_id, column_id, page_id", [path]).fetchall()
        names = [d[0] for d in connection.description]
        answer["page_index"] = [dict(zip(names, row)) for row in rows]

    json.dump(answer, sys.stdout, allow_nan=False)


if __name__ == "__main__":
    main(sys.argv[1])
