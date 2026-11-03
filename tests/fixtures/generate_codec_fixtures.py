"""Generates compression test fixtures with pyarrow and the Python standard library.

    pip install pyarrow
    python tests/fixtures/generate_codec_fixtures.py

1. Fixtures/Codecs/<case>.<ext>: the same inputs compressed by independent implementations
   (.snappy raw block, .gz RFC 1952, .br, .zst, .lz4 raw block). The inputs are NOT stored: the tests
   rebuild them with the formulas in CodecTestData, which must stay in sync with `cases` below.
2. Fixtures/pyarrow-codec-<codec>-v<page version>.parquet: real Parquet files whose pages use each
   codec (pyarrow cannot write LZ4_RAW, so that codec is only covered by the vectors above).
"""
import gzip
import pathlib

import pyarrow as pa
import pyarrow.parquet as pq

OUT = pathlib.Path(__file__).resolve().parent.parent / "ParquetNeo.Tests" / "Fixtures"
CODECS = OUT / "Codecs"
CODECS.mkdir(parents=True, exist_ok=True)


def lcg_bytes(n, seed):
    x = seed
    out = bytearray()
    for _ in range(n):
        x = (x * 1103515245 + 12345) & 0x7FFFFFFF
        out.append((x >> 16) & 0xFF)
    return bytes(out)


def mixed(n):
    """1000-byte blocks: every 5th is noise (seeded by its index), the others repeat a sentence."""
    sentence = b"the quick brown fox jumps over the lazy dog "
    out = bytearray()
    block = 0
    while len(out) < n:
        if block % 5 == 0:
            out += lcg_bytes(1000, 1000 + block)
        else:
            out += (sentence * 30)[:1000]
        block += 1
    return bytes(out[:n])


cases = {
    "empty": b"",
    "single": b"a",
    "run": b"a" * 1000,
    "text": b"hello parquet " * 200,
    "ramp": bytes(i % 256 for i in range(5000)),
    "noise": lcg_bytes(5000, 12345),
    "mixed": mixed(140_000),
}

for name, data in cases.items():
    for ext, codec in (("snappy", "snappy"), ("br", "brotli"), ("zst", "zstd"), ("lz4", "lz4_raw")):
        (CODECS / f"{name}.{ext}").write_bytes(pa.compress(data, codec=codec, asbytes=True))
    (CODECS / f"{name}.gz").write_bytes(gzip.compress(data, mtime=0))

# RFC 1952 allows several members back to back; readers should accept them.
(CODECS / "multimember.gz").write_bytes(gzip.compress(b"first part ", mtime=0) + gzip.compress(b"second part", mtime=0))

rows = 1000
table = pa.table(
    {
        "square": pa.array([i * i for i in range(rows)], pa.int64()),
        "ratio": pa.array([i * 0.25 for i in range(rows)], pa.float64()),
    },
    schema=pa.schema([pa.field("square", pa.int64(), nullable=False), pa.field("ratio", pa.float64(), nullable=False)]),
)
for codec in ("snappy", "gzip", "brotli", "zstd"):
    for page_version in ("1.0", "2.0"):
        pq.write_table(
            table,
            OUT / f"pyarrow-codec-{codec}-v{page_version[0]}.parquet",
            compression=codec,
            use_dictionary=False,
            data_page_version=page_version,
            write_statistics=False,
            row_group_size=rows,
            data_page_size=1 << 20,
            store_schema=False,
        )

print("done")
