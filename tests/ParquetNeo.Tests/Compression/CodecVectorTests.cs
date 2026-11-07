using ParquetNeo.Compression;
using ParquetNeo.Compression.Lz4;
using ParquetNeo.Compression.Snappy;
using ParquetNeo.Compression.Zstd;

namespace ParquetNeo.Tests.Compression;

/// <summary>Decompresses data produced by independent implementations (pyarrow, Python's gzip module).</summary>
public class CodecVectorTests
{
    [Test]
    [Arguments("snappy")]
    [Arguments("gz")]
    [Arguments("br")]
    [Arguments("zst")]
    [Arguments("lz4")]
    public async Task IndependentlyCompressedData_DecompressesToTheOriginal(string extension)
    {
        var codec = CodecFor(extension);

        foreach (var (name, original) in CodecTestData.Cases)
        {
            var decompressed = CodecTestData.Decompress(codec, CodecTestData.Vector(name, extension), original.Length);

            await Assert.That(decompressed.AsSpan().SequenceEqual(original)).IsTrue().Because($"{extension}/{name}");
        }
    }

    [Test]
    public async Task Gzip_AcceptsSeveralMembersBackToBack()
    {
        var data = CodecTestData.Vector("multimember", "gz");

        var decompressed = CodecTestData.Decompress(new GzipCodec(), data, "first part second part".Length);

        await Assert.That(System.Text.Encoding.ASCII.GetString(decompressed)).IsEqualTo("first part second part");
    }

    [Test]
    public async Task Snappy_OutputOfTheReferenceEncoder_MatchesOurs_ForSmallInputs()
    {
        // pyarrow's libsnappy output for 100 x 'a': preamble, literal 'a', two overlapping copies at offset 1.
        var hundred = Enumerable.Repeat((byte)'a', 100).ToArray();

        var ours = CodecTestData.Compress(new SnappyCodec(), hundred);

        await Assert.That(TestBytes.ToHex(ours)).IsEqualTo("640061FE01008A0100");
    }

    private static ICodec CodecFor(string extension) => extension switch
    {
        "snappy" => new SnappyCodec(),
        "gz" => new GzipCodec(),
        "br" => new BrotliCodec(),
        "zst" => new ZstdCodec(),
        "lz4" => new Lz4RawCodec(),
        _ => throw new ArgumentOutOfRangeException(nameof(extension)),
    };
}
