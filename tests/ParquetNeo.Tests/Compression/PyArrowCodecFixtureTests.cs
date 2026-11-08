using ParquetNeo.Compression;
using ParquetNeo.Compression.Snappy;
using ParquetNeo.Compression.Zstd;
using ParquetNeo.Encodings;
using ParquetNeo.Metadata;
using ParquetNeo.Metadata.Headers;

namespace ParquetNeo.Tests.Compression;

/// <summary>
/// Decompresses the pages of real pyarrow files with each codec, then decodes the values with the PLAIN
/// decoder. LZ4_RAW is not covered here because pyarrow cannot write it (see the vector tests instead).
/// </summary>
public class PyArrowCodecFixtureTests
{
    private const int Rows = 1000;

    [Test]
    [Arguments("snappy", CompressionCodec.Snappy, 1)]
    [Arguments("snappy", CompressionCodec.Snappy, 2)]
    [Arguments("gzip", CompressionCodec.Gzip, 1)]
    [Arguments("gzip", CompressionCodec.Gzip, 2)]
    [Arguments("brotli", CompressionCodec.Brotli, 1)]
    [Arguments("brotli", CompressionCodec.Brotli, 2)]
    [Arguments("zstd", CompressionCodec.Zstd, 1)]
    [Arguments("zstd", CompressionCodec.Zstd, 2)]
    public async Task CompressedPages_DecompressAndDecodeToTheOriginalValues(string name, CompressionCodec expected, int pageVersion)
    {
        var file = File.ReadAllBytes(TestBytes.FixturePath($"pyarrow-codec-{name}-v{pageVersion}.parquet"));
        var metadata = FileMetaData.Deserialize(TestBytes.ReadFooterBytes(file));
        var registry = new CodecRegistry().AddSnappy().AddZstd();

        var squares = ReadColumn(file, metadata, registry, "square", expected, 8);
        var ratios = ReadColumn(file, metadata, registry, "ratio", expected, 8);

        var decodedSquares = new long[Rows];
        var decodedRatios = new double[Rows];
        PlainDecoder.ReadInt64s(squares, decodedSquares);
        PlainDecoder.ReadDoubles(ratios, decodedRatios);

        await Assert.That(decodedSquares.AsSpan().SequenceEqual(Enumerable.Range(0, Rows).Select(i => (long)i * i).ToArray())).IsTrue();
        await Assert.That(decodedRatios.AsSpan().SequenceEqual(Enumerable.Range(0, Rows).Select(i => i * 0.25).ToArray())).IsTrue();
    }

    [Test]
    public async Task TheCompressedPages_AreSmallerThanTheirContent()
    {
        var file = File.ReadAllBytes(TestBytes.FixturePath("pyarrow-codec-zstd-v1.parquet"));
        var chunk = FileMetaData.Deserialize(TestBytes.ReadFooterBytes(file)).RowGroups[0].Columns[0].MetaData!;

        await Assert.That(chunk.TotalCompressedSize).IsLessThan(chunk.TotalUncompressedSize);
    }

    /// <summary>Returns the values bytes of the column's only page (a required column, so there are no levels).</summary>
    private static byte[] ReadColumn(byte[] file, FileMetaData metadata, CodecRegistry registry, string column, CompressionCodec expected, int valueSize)
    {
        var chunk = metadata.RowGroups[0].Columns.Single(c => c.MetaData!.PathInSchema[0] == column).MetaData!;
        var codec = registry.Get(chunk.Codec);

        var header = PageHeader.Deserialize(file.AsSpan((int)chunk.DataPageOffset), out int headerLength);
        var compressed = file.AsSpan((int)chunk.DataPageOffset + headerLength, header.CompressedPageSize);
        var values = new byte[Rows * valueSize];

        if (header.DataPageHeaderV2 is { } v2)
        {
            // v2: the levels (none here) are stored uncompressed; only the values section is compressed.
            int levels = v2.RepetitionLevelsByteLength + v2.DefinitionLevelsByteLength;
            if (v2.IsCompressed)
            {
                codec.Decompress(compressed[levels..], values);
            }
            else
            {
                compressed[levels..].CopyTo(values);
            }
        }
        else
        {
            codec.Decompress(compressed, values);
        }

        if (chunk.Codec != expected || header.UncompressedPageSize != values.Length)
        {
            throw new InvalidOperationException($"Unexpected codec {chunk.Codec} or page size {header.UncompressedPageSize}.");
        }

        return values;
    }
}
