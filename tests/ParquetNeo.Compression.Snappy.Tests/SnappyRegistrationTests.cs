using ParquetNeo.Tests.Compression;

namespace ParquetNeo.Compression.Snappy.Tests;

public class SnappyRegistrationTests
{
    [Test]
    public async Task TheExtensionMethod_RegistersTheCodec_AndReturnsTheRegistry()
    {
        var registry = new CodecRegistry();

        var returned = registry.AddSnappy();

        await Assert.That(returned).IsSameReferenceAs(registry);
        await Assert.That(registry.Get(CompressionCodec.Snappy)).IsTypeOf<SnappyCodec>();
        await Assert.That(registry.TryGet(CompressionCodec.Snappy, out var codec)).IsTrue();
        await Assert.That(codec!.Id).IsEqualTo(CompressionCodec.Snappy);
    }

    [Test]
    public async Task WithoutTheExtensionMethod_TheCodecIsNotThere_AndTheMessageNamesThePackage()
    {
        var exception = Assert.Throws<NotSupportedException>(() => new CodecRegistry().Get(CompressionCodec.Snappy));

        await Assert.That(exception!.Message).Contains("ParquetNeo.Compression.Snappy");
    }

    [Test]
    public async Task TheRegistry_IsSafeToUseFromManyThreads_WithThisCodecAndAnother()
    {
        var registry = new CodecRegistry().AddSnappy();
        var data = CodecTestData.Cases["mixed"];

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() =>
        {
            var codec = registry.Get(i % 2 == 0 ? CompressionCodec.Snappy : CompressionCodec.Gzip);
            var restored = CodecTestData.Decompress(codec, CodecTestData.Compress(codec, data), data.Length);
            return restored.AsSpan().SequenceEqual(data);
        })));

        await Assert.That(results.All(r => r)).IsTrue();
    }
}
