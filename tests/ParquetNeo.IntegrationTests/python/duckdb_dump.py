"""Reads a Parquet file with DuckDB, a second implementation, and prints what is in it as JSON, normalized as in normalize.py.

    python duckdb_dump.py file.parquet [column,column,...]

The columns named after the file are left out of the query: the ones DuckDB reads in a unit or a type of its own (a time in milliseconds is a time
in microseconds for it), which the test compares otherwise.
"""
import json
import sys

import duckdb

from normalize import normalize_table


def arrow(relation):
    """The result as an Arrow table; the method was renamed in DuckDB 2."""
    return relation.to_arrow_table() if hasattr(relation, "to_arrow_table") else relation.fetch_arrow_table()


def main(path, excluded):
    connection = duckdb.connect()
    described = connection.execute("DESCRIBE SELECT * FROM read_parquet(?)", [path]).fetchall()
    projection = []
    for name, kind in ((row[0], row[1]) for row in described):
        quoted = '"' + name.replace('"', '""') + '"'
        if name in excluded:
            continue
        # A UUID comes as text with dashes; the tests compare the 32 hex digits, as pyarrow gives them.
        projection.append("replace(CAST(%s AS VARCHAR), '-', '') AS %s" % (quoted, quoted) if kind == "UUID" else quoted)
    table = arrow(connection.sql("SELECT %s FROM read_parquet(?)" % ", ".join(projection), params=[path]))
    json.dump({
        "columns": normalize_table(table),
        "schema": [{"name": f.name, "type": str(f.type)} for f in table.schema],
        "duckdb": duckdb.__version__,
    }, sys.stdout, allow_nan=False)


if __name__ == "__main__":
    main(sys.argv[1], set(sys.argv[2].split(",")) if len(sys.argv) > 2 and sys.argv[2] else set())
