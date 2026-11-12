using ParquetNeo.Compression;

namespace ParquetNeo.Tests.Compression;

/// <summary>Decompresses data produced by independent implementations (pyarrow, Python's gzip module): the built-in codecs. The add-on codecs have theirs in their own test projects.</summary>
public class CodecVectorTests
{
    [Test]
    public Task Gzip_IndependentlyCompressedData_DecompressesToTheOriginal() =>
        CodecConformance.IndependentlyCompressedData_DecompressesToTheOriginal(new GzipCodec(), "gz");

    [Test]
    public Task Brotli_IndependentlyCompressedData_DecompressesToTheOriginal() =>
        CodecConformance.IndependentlyCompressedData_DecompressesToTheOriginal(new BrotliCodec(), "br");

    [Test]
    public async Task Gzip_AcceptsSeveralMembersBackToBack()
    {
        var data = CodecTestData.Vector("multimember", "gz");

        var decompressed = CodecTestData.Decompress(new GzipCodec(), data, "first part second part".Length);

        await Assert.That(System.Text.Encoding.ASCII.GetString(decompressed)).IsEqualTo("first part second part");
    }
}
