"""Writes the decimal and text dataset with pyarrow.

    pip install pyarrow
    python tests/fixtures/generate_decimal_text_fixtures.py

Output goes to tests/ParquetNeo.Tests/Fixtures.
"""
import pathlib

import pyarrow.parquet as pq

import decimal_text_dataset

OUT = pathlib.Path(__file__).resolve().parent.parent / "ParquetNeo.Tests" / "Fixtures"

for name, optional, options in [
    ("pyarrow-decimal-required-plain", False, dict(use_dictionary=False)),
    ("pyarrow-decimal-optional-plain", True, dict(use_dictionary=False)),
    ("pyarrow-decimal-optional-dictionary", True, dict(use_dictionary=True)),
    ("pyarrow-decimal-optional-v2-snappy", True, dict(use_dictionary=False, data_page_version="2.0", compression="snappy")),
]:
    pq.write_table(decimal_text_dataset.build(optional), OUT / f"{name}.parquet", store_schema=False, write_statistics=False,
                   store_decimal_as_integer=True, **options)
    print("wrote", name)
