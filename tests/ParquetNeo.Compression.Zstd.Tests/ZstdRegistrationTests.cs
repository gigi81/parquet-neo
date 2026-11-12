using ParquetNeo.Tests.Compression;

namespace ParquetNeo.Compression.Zstd.Tests;

public class ZstdRegistrationTests
{
    [Test]
    public async Task TheExtensionMethod_RegistersTheCodec_AndReturnsTheRegistry()
    {
        var registry = new CodecRegistry();

        var returned = registry.AddZstd(level: 5);

        await Assert.That(returned).IsSameReferenceAs(registry);
        await Assert.That(registry.Get(CompressionCodec.Zstd)).IsTypeOf<ZstdCodec>();
        await Assert.That(registry.TryGet(CompressionCodec.Zstd, out var codec)).IsTrue();
        await Assert.That(codec!.Id).IsEqualTo(CompressionCodec.Zstd);
    }

    [Test]
    public async Task WithoutTheExtensionMethod_TheCodecIsNotThere_AndTheMessageNamesThePackage()
    {
        var exception = Assert.Throws<NotSupportedException>(() => new CodecRegistry().Get(CompressionCodec.Zstd));

        await Assert.That(exception!.Message).Contains("ParquetNeo.Compression.Zstd");
    }
}
