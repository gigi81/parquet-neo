"""Writes the logical type dataset with pyarrow.

    pip install pyarrow
    python tests/fixtures/generate_logical_fixtures.py

Output goes to tests/ParquetNeo.Tests/Fixtures.
"""
import pathlib

import pyarrow.parquet as pq

import logical_dataset

OUT = pathlib.Path(__file__).resolve().parent.parent / "ParquetNeo.Tests" / "Fixtures"

for name, optional, options in [
    ("pyarrow-logical-required-plain", False, dict(use_dictionary=False)),
    ("pyarrow-logical-optional-plain", True, dict(use_dictionary=False)),
    ("pyarrow-logical-optional-dictionary", True, dict(use_dictionary=True)),
    ("pyarrow-logical-optional-v2-snappy", True, dict(use_dictionary=False, data_page_version="2.0", compression="snappy")),
]:
    pq.write_table(logical_dataset.build(optional), OUT / f"{name}.parquet", store_schema=False, write_statistics=False, **options)
    print("wrote", name)
