"""Checks with pyarrow how a file with the legacy list layouts reads back (the rows of LegacyListFile in the test project).

    python tests/fixtures/verify_legacy_lists.py file.parquet
"""
import sys

import pyarrow.parquet as pq

ROWS = 24


def expected():
    two_level, struct_element, tuple_, array, implicit = [], [], [], [], []
    for i in range(ROWS):
        two_level.append(None if i % 3 == 0 else [] if i % 3 == 1 else [i, i + 1])
        struct_element.append(None if i % 3 == 0 else [] if i % 3 == 1 else [{"str": f"s{i}", "num": i}, {"str": "t", "num": -i}])
        tuple_.append(None if i % 2 == 0 else [{"str": f"a{i}"}, {"str": "b"}])
        array.append([] if i % 2 == 0 else [{"str": f"x{i}"}])
        implicit.append([i * 10 + n for n in range(i % 3)])
    return {"two_level": two_level, "struct_element": struct_element, "tuple": tuple_, "array": array, "implicit": implicit}


status = 0
for path in sys.argv[1:]:
    table = pq.read_table(path)
    problems = []
    for name, want in expected().items():
        got = table.column(name).to_pylist()
        if got != want:
            first = next(i for i, (a, b) in enumerate(zip(got, want)) if a != b)
            problems.append(f"{name} differs at row {first}: {got[first]!r} != {want[first]!r}")
    print(path, "OK" if not problems else "; ".join(problems))
    status |= bool(problems)
sys.exit(status)
