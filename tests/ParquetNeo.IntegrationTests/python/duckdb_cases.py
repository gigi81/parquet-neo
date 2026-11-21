"""Files written by DuckDB, a second implementation of Parquet (its own C++ code, not Arrow's), for ParquetNeo to read.

DuckDB writes what pyarrow does not by default: bloom filters for dictionary columns, data pages V2 with the delta encodings, field ids, and
lists, structs and maps in its own way. What is in each file is the result of the SQL query it was made from, read as an Arrow table and
normalized as in normalize.py.
"""
import os

import duckdb

BASIC = """
SELECT
    i::BIGINT AS Id,
    ((i * 7) % 256 - 128)::TINYINT AS I8,
    ((i * 131) % 65536 - 32768)::SMALLINT AS I16,
    CASE WHEN i % 7 = 0 THEN NULL ELSE (i * 997 - 2000000)::INTEGER END AS I32,
    ((i * 13) % 256)::UTINYINT AS U8,
    ((i * 257) % 65536)::USMALLINT AS U16,
    ((i * 2654435761) % 4294967296)::UINTEGER AS U32,
    ((i * 7919) % 1000000007)::UBIGINT AS U64,
    CASE WHEN i % 11 = 0 THEN NULL ELSE (i * 0.25 - 100)::FLOAT END AS F32,
    CASE WHEN i % 13 = 0 THEN NULL ELSE (i * 0.1 - 50)::DOUBLE END AS F64,
    CASE WHEN i % 10 = 0 THEN NULL ELSE i % 3 = 0 END AS B,
    CASE WHEN i % 13 = 0 THEN NULL WHEN i % 17 = 0 THEN '' ELSE 'row ' || i || ' é€ ü 😀' END AS S,
    CASE WHEN i % 15 = 0 THEN NULL ELSE repeat(chr((65 + i % 26)::INTEGER), i % 9)::BLOB END AS Bin,
    CASE WHEN i % 9 = 0 THEN NULL ELSE (((i * 7919) % 100000000) / 100.0)::DECIMAL(9, 2) END AS Dec9,
    CASE WHEN i % 9 = 0 THEN NULL ELSE (((i * 7919) % 100000000000) / 10000.0)::DECIMAL(18, 4) END AS Dec18,
    (DATE '2000-01-01' + (i % 5000)::INTEGER) AS D,
    (TIMESTAMP '2020-01-01 00:00:00' + to_microseconds(i * 1234567)) AS Ts
FROM range(5000) t(i)
"""

# The shapes of the tests of the nested rows: the same names and types as the classes of the tests.
NESTED = {
    "lists": """
SELECT
    i::INTEGER AS Id,
    CASE WHEN i % 11 = 0 THEN NULL WHEN i % 7 = 0 THEN []::VARCHAR[]
         ELSE list_transform(range(i % 4 + 1), lambda j: CASE WHEN (i + j) % 5 = 0 THEN NULL ELSE 't' || ((i + j) % 9) END) END AS Tags,
    CASE WHEN i % 13 = 0 THEN NULL
         ELSE list_transform(range(i % 5), lambda j: CASE WHEN (i + j) % 6 = 0 THEN NULL ELSE (i + j)::INTEGER END) END AS Nums
FROM range(300) t(i)""",
    "struct": """
SELECT
    i::INTEGER AS Id,
    CASE WHEN i % 9 = 0 THEN NULL ELSE {
        'Name': 'p' || i,
        'Age': CASE WHEN i % 8 = 0 THEN NULL ELSE (20 + i % 50)::INTEGER END,
        'Home': CASE WHEN i % 4 = 0 THEN NULL ELSE {
            'City': CASE WHEN i % 10 = 1 THEN NULL ELSE 'city ' || (i % 20) END,
            'Zip': CASE WHEN i % 3 = 0 THEN NULL ELSE i::INTEGER END } END } END AS Person
FROM range(300) t(i)""",
    "map": """
SELECT
    i::INTEGER AS Id,
    CASE WHEN i % 10 = 0 THEN NULL
         ELSE map_from_entries(list_transform(range(i % 4), lambda j: {'k': 'k' || (j), 'v': CASE WHEN (i + j) % 7 = 0 THEN NULL ELSE (i * j)::INTEGER END})) END AS Scores
FROM range(300) t(i)""",
    "list_struct": """
SELECT
    i::INTEGER AS Id,
    CASE WHEN i % 12 = 0 THEN NULL
         ELSE list_transform(range(i % 4), lambda j: CASE WHEN (i + j) % 8 = 0 THEN NULL
              ELSE {'Sku': 's' || (i + j), 'Qty': CASE WHEN j % 3 = 2 THEN NULL ELSE (i % 7 + j)::INTEGER END } END) END AS Items
FROM range(300) t(i)""",
}


def generate(directory, record, skipped):
    """Writes the files, adds them to the manifest with record(directory, name, table, kind, shape, writer, options); gives the version of DuckDB."""
    connection = duckdb.connect()

    def copy(name, query, options, kind="flat", shape=None):
        path = os.path.join(directory, name + ".parquet").replace("'", "''")
        # A query or an option that DuckDB refuses is an error and not a skipped case: a file that is quietly missing tests nothing.
        connection.execute("COPY (%s) TO '%s' (FORMAT parquet, %s)" % (query, path, options))
        relation = connection.sql(query)
        table = relation.to_arrow_table() if hasattr(relation, "to_arrow_table") else relation.fetch_arrow_table()
        record(directory, name, table, kind, shape, "duckdb", {"copy_options": options})

    for codec in ("uncompressed", "snappy", "gzip", "zstd", "lz4_raw"):
        for version in ("V1", "V2"):
            copy("duckdb_basic_%s_%s" % (codec, version.lower()), BASIC, "COMPRESSION %s, PARQUET_VERSION %s, ROW_GROUP_SIZE 1200" % (codec, version))

    for shape, query in NESTED.items():
        copy("duckdb_nested_%s_v1" % shape, query, "COMPRESSION snappy, ROW_GROUP_SIZE 100", kind="nested", shape=shape)
        copy("duckdb_nested_%s_v2" % shape, query, "COMPRESSION zstd, PARQUET_VERSION V2, ROW_GROUP_SIZE 128", kind="nested", shape=shape)

    # Features: key-value metadata, field ids, a dictionary that is given up, bloom filters on unsorted values.
    copy("duckdb_kv_and_field_ids", "SELECT i::BIGINT AS Id, 'n' || i AS Name FROM range(100) t(i)",
         "KV_METADATA {owner: 'tests', note: 'hello'}, FIELD_IDS {Id: 5, Name: 9}")
    copy("duckdb_dictionary_fallback", "SELECT i::INTEGER AS Id, CASE WHEN i < 5000 THEN 'repeat ' || (i % 4) ELSE 'value ' || ((i * 7919) % 20011) END AS S FROM range(20000) t(i)",
         "DICTIONARY_SIZE_LIMIT 1000, ROW_GROUP_SIZE 10000")
    # DuckDB writes a bloom filter only for the columns it stores with a dictionary, so the values repeat: each row group of 2048 rows has 300 different ones,
    # the even numbers of a stretch that moves along from group to group, so that the odd numbers are in the range of every group but in none of them.
    copy("duckdb_bloom_filters",
         "SELECT v AS Id, 'name ' || v AS Name FROM (SELECT (2 * (((i // 2048) * 37 + (i * 7919) % 300) % 5000))::BIGINT AS v FROM range(20480) t(i))",
         "ROW_GROUP_SIZE 2048, COMPRESSION snappy")
    for kind, query in (("empty", "SELECT 1::BIGINT AS Id, 'x' AS S WHERE false"), ("single_row", "SELECT 42::BIGINT AS Id, 'only' AS S")):
        copy("duckdb_" + kind, query, "COMPRESSION zstd")

    return duckdb.__version__
