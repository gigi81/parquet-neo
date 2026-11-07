"""Writes the encoding dataset with pyarrow, one column encoding each.

    pip install pyarrow
    python tests/fixtures/generate_encoding_fixtures.py

Output goes to tests/ParquetNeo.Tests/Fixtures.
"""
import pathlib

import pyarrow.parquet as pq

import encoding_dataset

OUT = pathlib.Path(__file__).resolve().parent.parent / "ParquetNeo.Tests" / "Fixtures"

for name, optional, options in [
    ("pyarrow-encodings-required-v1", False, dict(data_page_version="1.0")),
    ("pyarrow-encodings-optional-v1", True, dict(data_page_version="1.0")),
    ("pyarrow-encodings-optional-v2-smallpages", True, dict(data_page_version="2.0", data_page_size=800, write_batch_size=64)),
    ("pyarrow-encodings-required-v2-snappy", False, dict(data_page_version="2.0", compression="snappy")),
]:
    pq.write_table(
        encoding_dataset.build(optional),
        OUT / f"{name}.parquet",
        use_dictionary=False,
        column_encoding=encoding_dataset.encodings(),
        write_statistics=False,
        store_schema=False,
        **options,
    )
    print("wrote", name)
