"""The nested sample dataset shared by the generator and the checker. The formulas are mirrored in the test project
(tests/ParquetNeo.Tests/Core/Nested/NestedDataset.cs), so change both together."""
import pyarrow as pa

ROWS = 200


def scores(i):
    return [None, [], [i], [i, None, i + 1], [None], list(range(i % 5 + 3))][i % 6]


def matrix(i):
    return [None, [], [[1, 2], [3]], [None, [i]], [[i], [], [i + 1, i + 2, i + 3]]][i % 5]


def person(i):
    return [
        None,
        {"name": f"n{i}", "address": {"city": f"c{i % 3}", "zip": i}},
        {"name": None, "address": None},
        {"name": f"n{i}", "address": {"city": None, "zip": None}},
    ][i % 4]


def events(i):
    return [
        None,
        [],
        [{"kind": f"k{i % 3}", "count": i}],
        [{"kind": None, "count": i}, {"kind": "x", "count": None}, None],
        [{"kind": f"k{i % 2}", "count": i + n} for n in range(i % 3 + 1)],
    ][i % 5]


def attributes(i):
    return [None, [], [("a", i), ("b", None)], [(f"k{i % 5}", i), ("z", 1)]][i % 4]


def labels(i):
    return [[], [f"l{i}"], ["a", "b", "c"]][i % 3]


def build():
    event = pa.struct([("kind", pa.string()), ("count", pa.int32())])
    schema = pa.schema(
        [
            pa.field("id", pa.int64(), nullable=False),
            pa.field("scores", pa.list_(pa.int32())),
            pa.field("matrix", pa.list_(pa.list_(pa.int32()))),
            pa.field("person", pa.struct([("name", pa.string()), ("address", pa.struct([("city", pa.string()), ("zip", pa.int32())]))])),
            pa.field("events", pa.list_(event)),
            pa.field("attributes", pa.map_(pa.string(), pa.int32())),
            pa.field("labels", pa.list_(pa.field("element", pa.string(), nullable=False)), nullable=False),
        ]
    )
    columns = [
        pa.array(list(range(ROWS)), pa.int64()),
        pa.array([scores(i) for i in range(ROWS)], schema.field("scores").type),
        pa.array([matrix(i) for i in range(ROWS)], schema.field("matrix").type),
        pa.array([person(i) for i in range(ROWS)], schema.field("person").type),
        pa.array([events(i) for i in range(ROWS)], schema.field("events").type),
        pa.array([attributes(i) for i in range(ROWS)], schema.field("attributes").type),
        pa.array([labels(i) for i in range(ROWS)], schema.field("labels").type),
    ]
    return pa.table(columns, schema=schema)
