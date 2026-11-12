using ParquetNeo.Compression;

namespace ParquetNeo.Tests.Compression;

public class CodecRegistryTests
{
    [Test]
    public async Task ANewRegistry_HasTheBuiltInCodecs()
    {
        var registry = new CodecRegistry();

        foreach (var id in new[] { CompressionCodec.Uncompressed, CompressionCodec.Gzip, CompressionCodec.Brotli })
        {
            await Assert.That(registry.Get(id).Id).IsEqualTo(id);
        }

        // Snappy, Zstd and LZ4_RAW come from add-on packages and are not registered by default.
        await Assert.That(registry.TryGet(CompressionCodec.Snappy, out _)).IsFalse();
    }

    [Test]
    public async Task UnregisteredCodecs_ExplainWhatToDo()
    {
        var registry = new CodecRegistry();

        var snappy = Assert.Throws<NotSupportedException>(() => registry.Get(CompressionCodec.Snappy));
        var zstd = Assert.Throws<NotSupportedException>(() => registry.Get(CompressionCodec.Zstd));
        var lz4Raw = Assert.Throws<NotSupportedException>(() => registry.Get(CompressionCodec.Lz4Raw));
        var lz4 = Assert.Throws<NotSupportedException>(() => registry.Get(CompressionCodec.Lz4));
        var lzo = Assert.Throws<NotSupportedException>(() => registry.Get(CompressionCodec.Lzo));
        var unknown = Assert.Throws<NotSupportedException>(() => registry.Get((CompressionCodec)99));

        await Assert.That(snappy.Message).Contains("ParquetNeo.Compression.Snappy");
        await Assert.That(zstd.Message).Contains("ParquetNeo.Compression.Zstd");
        await Assert.That(lz4Raw.Message).Contains("ParquetNeo.Compression.Lz4");
        await Assert.That(lz4.Message).Contains("LZ4_RAW");
        await Assert.That(lzo.Message).Contains("LZO");
        await Assert.That(unknown.Message).Contains("99");
        await Assert.That(registry.TryGet(CompressionCodec.Zstd, out _)).IsFalse();
    }

    [Test]
    public async Task Register_ReplacesAnExistingCodec_AndAllowsChaining()
    {
        var registry = new CodecRegistry();
        var custom = new GzipCodec(System.IO.Compression.CompressionLevel.Fastest);

        var returned = registry.Register(custom);

        await Assert.That(returned).IsSameReferenceAs(registry);
        await Assert.That(registry.Get(CompressionCodec.Gzip)).IsSameReferenceAs(custom);
    }

    [Test]
    public async Task CustomCodecs_CanBeRegisteredForAnyId()
    {
        var registry = new CodecRegistry().Register(new ReversingCodec());

        var codec = registry.Get(CompressionCodec.Lzo);
        var compressed = CodecTestData.Compress(codec, [1, 2, 3]);

        await Assert.That(compressed).IsEquivalentTo(new byte[] { 3, 2, 1 });
        await Assert.That(CodecTestData.Decompress(codec, compressed, 3)).IsEquivalentTo(new byte[] { 1, 2, 3 });
    }

    [Test]
    public async Task RegisteringNull_IsRejected()
    {
        await Assert.That(() => new CodecRegistry().Register(null!)).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task Default_IsASharedInstanceWithTheBuiltIns()
    {
        await Assert.That(CodecRegistry.Default).IsSameReferenceAs(CodecRegistry.Default);
        await Assert.That(CodecRegistry.Default.Get(CompressionCodec.Gzip)).IsTypeOf<GzipCodec>();
    }

    [Test]
    public async Task Registry_IsSafeToUseFromManyThreads()
    {
        var registry = new CodecRegistry();
        var data = CodecTestData.Cases["mixed"];

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(i => Task.Run(() =>
        {
            var codec = registry.Get(i % 2 == 0 ? CompressionCodec.Brotli : CompressionCodec.Gzip);
            var restored = CodecTestData.Decompress(codec, CodecTestData.Compress(codec, data), data.Length);
            return restored.AsSpan().SequenceEqual(data);
        })));

        await Assert.That(results.All(r => r)).IsTrue();
    }

    private sealed class ReversingCodec : ICodec
    {
        public CompressionCodec Id => CompressionCodec.Lzo;

        public int GetMaxCompressedLength(int uncompressedLength) => uncompressedLength;

        public int Compress(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            source.CopyTo(destination);
            destination[..source.Length].Reverse();
            return source.Length;
        }

        public void Decompress(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            source.CopyTo(destination);
            destination.Reverse();
        }
    }
}
