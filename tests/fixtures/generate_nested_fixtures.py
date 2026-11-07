"""Writes the nested sample dataset with pyarrow.

    pip install pyarrow
    python tests/fixtures/generate_nested_fixtures.py

Output goes to tests/ParquetNeo.Tests/Fixtures.
"""
import pathlib

import pyarrow.parquet as pq

import nested_dataset

OUT = pathlib.Path(__file__).resolve().parent.parent / "ParquetNeo.Tests" / "Fixtures"
table = nested_dataset.build()

for name, options in [
    ("pyarrow-nested-v1", dict(data_page_version="1.0", use_dictionary=False)),
    ("pyarrow-nested-dictionary-v1", dict(data_page_version="1.0", use_dictionary=True)),
    ("pyarrow-nested-v2-snappy", dict(data_page_version="2.0", compression="snappy", use_dictionary=False, data_page_size=200, write_batch_size=16)),
    ("pyarrow-nested-v1-smallpages", dict(data_page_version="1.0", use_dictionary=False, data_page_size=200, write_batch_size=16)),
]:
    pq.write_table(table, OUT / f"{name}.parquet", store_schema=False, **options)
    print("wrote", name)
