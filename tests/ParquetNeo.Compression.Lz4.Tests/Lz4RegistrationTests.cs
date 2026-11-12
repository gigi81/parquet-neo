using ParquetNeo.Tests.Compression;

namespace ParquetNeo.Compression.Lz4.Tests;

public class Lz4RegistrationTests
{
    [Test]
    public async Task TheExtensionMethod_RegistersTheCodec_AndReturnsTheRegistry()
    {
        var registry = new CodecRegistry();

        var returned = registry.AddLz4Raw();

        await Assert.That(returned).IsSameReferenceAs(registry);
        await Assert.That(registry.Get(CompressionCodec.Lz4Raw)).IsTypeOf<Lz4RawCodec>();
        await Assert.That(registry.TryGet(CompressionCodec.Lz4Raw, out var codec)).IsTrue();
        await Assert.That(codec!.Id).IsEqualTo(CompressionCodec.Lz4Raw);
    }

    [Test]
    public async Task WithoutTheExtensionMethod_TheCodecIsNotThere_AndTheMessageNamesThePackage()
    {
        var exception = Assert.Throws<NotSupportedException>(() => new CodecRegistry().Get(CompressionCodec.Lz4Raw));

        await Assert.That(exception!.Message).Contains("ParquetNeo.Compression.Lz4");
    }
}
