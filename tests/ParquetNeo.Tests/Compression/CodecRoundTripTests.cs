using System.IO.Compression;
using ParquetNeo.Compression;
using ParquetNeo.Exceptions;

namespace ParquetNeo.Tests.Compression;

/// <summary>The built-in codecs: what every codec must do (<see cref="CodecConformance"/>), and what is particular to gzip and to the uncompressed one.</summary>
public class CodecRoundTripTests
{
    [Test]
    public Task EveryCodec_RoundTripsEveryCase_WithinItsMaximumLength() =>
        CodecConformance.EveryCase_RoundTrips_WithinTheMaximumLength(BuiltInCodecs.All);

    [Test]
    public Task EveryCodec_ReallyShrinksCompressibleData() => CodecConformance.CompressibleData_ReallyShrinks(BuiltInCodecs.Compressing);

    [Test]
    public Task IncompressibleData_StaysWithinTheMaximumLength() =>
        CodecConformance.IncompressibleData_StaysWithinTheMaximumLength(BuiltInCodecs.All.Where(c => c.Name != "brotli-9"));

    [Test]
    public Task EmptyInput_ProducesAValidNonEmptyStream_ForEveryCodec() => CodecConformance.EmptyInput_ProducesAValidNonEmptyStream(BuiltInCodecs.Compressing);

    [Test]
    public Task EveryCodec_RoundTripsInputsAroundBlockBoundaries() =>
        CodecConformance.InputsAroundBlockBoundaries_RoundTrip(new[] { "gzip", "brotli-0" }.Select(BuiltInCodecs.Named));

    [Test]
    public Task RandomStructuredInputs_RoundTrip() =>
        CodecConformance.RandomStructuredInputs_RoundTrip(new[] { "gzip-fastest", "brotli-0" }.Select(BuiltInCodecs.Named));

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
    public async Task GzipEmptyMember_MatchesWhatPythonWrites()
    {
        // The reference bytes from Python's gzip.compress(b"", mtime=0).
        var reference = CodecTestData.Vector("empty", "gz");

        var ours = CodecTestData.Compress(new GzipCodec(), []);

        await Assert.That(TestBytes.ToHex(ours)).IsEqualTo(TestBytes.ToHex(reference));
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
        await CodecConformance.ReportsItsParquetId(BuiltInCodecs.Named("gzip").Codec, CompressionCodec.Gzip);
        await CodecConformance.ReportsItsParquetId(BuiltInCodecs.Named("brotli").Codec, CompressionCodec.Brotli);
    }

    [Test]
    public Task GzipCompress_RejectsADestinationThatMightBeTooSmall() =>
        CodecConformance.Compress_RejectsADestinationThatMightBeTooSmall(BuiltInCodecs.Named("gzip").Codec);
}
