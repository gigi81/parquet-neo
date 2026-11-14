# Compression

Gzip and Brotli are built in. Snappy, LZ4_RAW and Zstandard are in separate packages so that you only get the libraries you use.
A codec is registered in the `CodecRegistry` that the options use, and chosen by `ParquetWriterOptions.Compression`:

```csharp
using ParquetNeo.Compression;
using ParquetNeo.Compression.Zstd;
using ParquetNeo.Serialization;

var codecs = new CodecRegistry().AddZstd(level: 3);

ParquetSerializer.Serialize(rows, stream, new ParquetSerializerOptions
{
    Writer = new ParquetWriterOptions { Compression = CompressionCodec.Zstd, Codecs = codecs },
});

var back = ParquetSerializer.Deserialize<Row>(stream, new ParquetDeserializerOptions
{
    Reader = new ParquetReaderOptions { Codecs = codecs },
});
```

| Codec | Package | Register with |
|---|---|---|
| Gzip, Brotli | `ParquetNeo` | already registered |
| Snappy | `ParquetNeo.Compression.Snappy` | `AddSnappy()` |
| LZ4_RAW | `ParquetNeo.Compression.Lz4` | `AddLz4Raw()` |
| Zstandard | `ParquetNeo.Compression.Zstd` | `AddZstd(level)` |

LZ4 with the Hadoop framing and LZO are not supported. LZ4_RAW is.

Pages are compressed one by one, so a reader can skip the pages and the row groups it does not need without decompressing them.
