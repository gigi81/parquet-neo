using System.IO.Compression;
using ParquetNeo.Compression;
using ParquetNeo.Exceptions;
using ParquetNeo.Metadata;

namespace ParquetNeo.Tests.Compression;

public class CodecRoundTripTests
{
    [Test]
    public async Task EveryCodec_RoundTripsEveryCase_WithinItsMaximumLength()
    {
        foreach (var (codecName, codec) in CodecTestData.Codecs)
        {
            foreach (var (caseName, original) in CodecTestData.Cases)
            {
                var compressed = CodecTestData.Compress(codec, original);
                var decompressed = CodecTestData.Decompress(codec, compressed, original.Length);

                await Assert.That(compressed.Length).IsLessThanOrEqualTo(codec.GetMaxCompressedLength(original.Length))
                    .Because($"{codecName}/{caseName} size bound");
                await Assert.That(decompressed.AsSpan().SequenceEqual(original)).IsTrue().Because($"{codecName}/{caseName}");
            }
        }
    }

    [Test]
    public async Task EveryCodec_ReallyShrinksCompressibleData()
    {
        foreach (var (codecName, codec) in CodecTestData.Codecs.Where(c => c.Name != "uncompressed"))
        {
            foreach (string caseName in new[] { "run", "text", "mixed" })
            {
                var original = CodecTestData.Cases[caseName];
                var compressed = CodecTestData.Compress(codec, original);

                await Assert.That(compressed.Length).IsLessThan(original.Length / 2).Because($"{codecName}/{caseName}");
            }
        }
    }

    [Test]
    public async Task IncompressibleData_StaysWithinTheMaximumLength()
    {
        var noise = CodecTestData.Lcg(1 << 20, 99); // 1 MB, several Snappy blocks

        foreach (var (codecName, codec) in CodecTestData.Codecs.Where(c => c.Name is not ("brotli-9" or "zstd-19")))
        {
            var compressed = CodecTestData.Compress(codec, noise);

            await Assert.That(compressed.Length).IsLessThanOrEqualTo(codec.GetMaxCompressedLength(noise.Length)).Because(codecName);
            await Assert.That(CodecTestData.Decompress(codec, compressed, noise.Length).AsSpan().SequenceEqual(noise))
                .IsTrue().Because(codecName);
        }
    }

    [Test]
    public async Task GzipMaxLength_CoversTheWorstCaseOfAnyDeflateEncoder()
    {
        // Fixed-Huffman blocks spend 9 bits on bytes 144-255, so a DEFLATE encoder with no stored-block fallback
        // can grow random data by 12.5% (zlib-ng's fastest level reaches ~5.5%). Plus 18 bytes of gzip framing.
        var gzip = new GzipCodec();

        foreach (int n in new[] { 0, 1, 10, 100, 5000, 70_000, 1 << 20, 100_000_000 })
        {
            await Assert.That(gzip.GetMaxCompressedLength(n)).IsGreaterThanOrEqualTo((int)(n * 9L / 8) + 18).Because($"n={n}");
        }
    }

    [Test]
    public async Task Gzip_StaysWithinItsBound_AtEveryLevel_ForIncompressibleData()
    {
        foreach (var level in new[] { CompressionLevel.NoCompression, CompressionLevel.Fastest, CompressionLevel.Optimal, CompressionLevel.SmallestSize })
        {
            var codec = new GzipCodec(level);
            foreach (int size in new[] { 1, 100, 5000, 70_000, 1 << 20 })
            {
                var noise = CodecTestData.Lcg(size, (uint)size);

                var compressed = CodecTestData.Compress(codec, noise);

                await Assert.That(compressed.Length).IsLessThanOrEqualTo(codec.GetMaxCompressedLength(size)).Because($"{level}/{size}");
                await Assert.That(CodecTestData.Decompress(codec, compressed, size).AsSpan().SequenceEqual(noise)).IsTrue().Because($"{level}/{size}");
            }
        }
    }

    [Test]
    public async Task EmptyInput_ProducesAValidNonEmptyStream_ForEveryCodec()
    {
        // Other readers reject a zero-byte compressed page for a codec that frames its data.
        foreach (var (name, codec) in CodecTestData.Codecs.Where(c => c.Name != "uncompressed"))
        {
            var compressed = CodecTestData.Compress(codec, []);

            await Assert.That(compressed.Length).IsGreaterThan(0).Because(name);
        }
    }

    [Test]
    public async Task GzipEmptyMember_MatchesWhatPythonWrites()
    {
        // The reference bytes from Python's gzip.compress(b"", mtime=0).
        var reference = CodecTestData.Vector("empty", "gz");

        var ours = CodecTestData.Compress(new GzipCodec(), []);

        await Assert.That(TestBytes.ToHex(ours)).IsEqualTo(TestBytes.ToHex(reference));
    }

    [Test]
    public async Task EveryCodec_RoundTripsInputsAroundBlockBoundaries()
    {
        // 64 KB is Snappy's block size; the small sizes cover the "too short to match" threshold.
        int[] sizes = [1, 2, 3, 4, 5, 14, 15, 16, 17, 31, 32, 33, 255, 256, 257, 65535, 65536, 65537, 131071, 131072, 131073];
        foreach (var (codecName, codec) in CodecTestData.Codecs.Where(c => c.Name is "snappy" or "gzip" or "brotli-0" or "zstd" or "lz4raw"))
        {
            foreach (int size in sizes)
            {
                // Half repetitive, half noisy, so both literals and long matches occur.
                var data = new byte[size];
                var noise = CodecTestData.Lcg(size, (uint)size);
                for (int i = 0; i < size; i++)
                {
                    data[i] = (i / 97) % 2 == 0 ? (byte)(i % 13) : noise[i];
                }

                var restored = CodecTestData.Decompress(codec, CodecTestData.Compress(codec, data), size);

                await Assert.That(restored.AsSpan().SequenceEqual(data)).IsTrue().Because($"{codecName}/{size}");
            }
        }
    }

    [Test]
    public async Task RandomStructuredInputs_RoundTrip()
    {
        var random = new Random(77);
        foreach (var (codecName, codec) in CodecTestData.Codecs.Where(c => c.Name is "snappy" or "gzip-fastest" or "brotli-0" or "zstd-1" or "lz4raw"))
        {
            for (int attempt = 0; attempt < 60; attempt++)
            {
                var data = RandomStructured(random, random.Next(0, 90_000));

                var restored = CodecTestData.Decompress(codec, CodecTestData.Compress(codec, data), data.Length);

                await Assert.That(restored.AsSpan().SequenceEqual(data)).IsTrue().Because($"{codecName} attempt {attempt}");
            }
        }
    }

    [Test]
    public async Task UncompressedCodec_RejectsASizeMismatch()
    {
        var codec = new UncompressedCodec();

        await Assert.That(() => codec.Decompress(new byte[4], new byte[5])).Throws<CompressionException>();
        await Assert.That(codec.Id).IsEqualTo(CompressionCodec.Uncompressed);
    }

    [Test]
    public async Task EveryCodec_ReportsItsParquetId()
    {
        (string Name, CompressionCodec Expected)[] expected =
        [
            ("snappy", CompressionCodec.Snappy), ("gzip", CompressionCodec.Gzip), ("brotli", CompressionCodec.Brotli),
            ("zstd", CompressionCodec.Zstd), ("lz4raw", CompressionCodec.Lz4Raw),
        ];

        foreach (var (name, id) in expected)
        {
            await Assert.That(CodecTestData.Codecs.First(c => c.Name == name).Codec.Id).IsEqualTo(id);
        }
    }

    [Test]
    public async Task Compress_RejectsADestinationThatMightBeTooSmall()
    {
        foreach (var name in new[] { "snappy", "gzip", "zstd" })
        {
            var codec = CodecTestData.Codecs.First(c => c.Name == name).Codec;

            await Assert.That(() => codec.Compress(new byte[100], new byte[10])).Throws<ArgumentException>().Because(name);
        }
    }

    private static byte[] RandomStructured(Random random, int length)
    {
        var data = new List<byte>(length);
        while (data.Count < length)
        {
            int run = Math.Min(length - data.Count, random.Next(1, 600));
            switch (random.Next(4))
            {
                case 0: // a single repeated byte
                    data.AddRange(Enumerable.Repeat((byte)random.Next(256), run));
                    break;
                case 1: // noise
                    data.AddRange(Enumerable.Range(0, run).Select(_ => (byte)random.Next(256)));
                    break;
                case 2 when data.Count > 8: // a copy of earlier data, possibly overlapping itself
                    int from = random.Next(0, data.Count);
                    for (int i = 0; i < run; i++)
                    {
                        data.Add(data[from + i % Math.Max(1, data.Count - from)]);
                    }

                    break;
                default: // a short repeating pattern
                    var pattern = Enumerable.Range(0, random.Next(2, 9)).Select(_ => (byte)random.Next(256)).ToArray();
                    data.AddRange(Enumerable.Range(0, run).Select(i => pattern[i % pattern.Length]));
                    break;
            }
        }

        return data.ToArray();
    }
}
