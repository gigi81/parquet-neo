using System.IO.Compression;
using ParquetNeo.Compression;

namespace ParquetNeo.Tests.Compression;

/// <summary>The gzip codec reads and writes the spans it is given through arrays from the pool; these check that it touches nothing else.</summary>
public class GzipCodecTests
{
    private const byte Sentinel = 0xAB;

    [Test]
    [Arguments(CompressionLevel.Optimal)]
    [Arguments(CompressionLevel.Fastest)]
    public async Task Compress_WritesOnlyTheCompressedBytes_AndLeavesTheRestOfTheDestinationAlone(CompressionLevel level)
    {
        var codec = new GzipCodec(level);
        var data = CodecTestData.Cases["mixed"];
        var destination = new byte[codec.GetMaxCompressedLength(data.Length) + 100];
        Array.Fill(destination, Sentinel);

        int length = codec.Compress(data, destination);

        await Assert.That(length).IsLessThan(data.Length);
        await Assert.That(destination.AsSpan(length).ToArray().All(b => b == Sentinel)).IsTrue();
        await Assert.That(CodecTestData.Decompress(codec, destination[..length], data.Length).AsSpan().SequenceEqual(data)).IsTrue();
    }

    [Test]
    public async Task Decompress_WritesInsideItsDestinationOnly_AndReadsInsideItsSource()
    {
        var codec = new GzipCodec();
        var data = CodecTestData.Cases["text"];
        var compressed = CodecTestData.Compress(codec, data);

        // The source sits in the middle of an array of other bytes, and so does the destination.
        var sourceArea = new byte[compressed.Length + 20];
        Array.Fill(sourceArea, (byte)0xFF);
        compressed.CopyTo(sourceArea, 10);
        var destinationArea = new byte[data.Length + 20];
        Array.Fill(destinationArea, Sentinel);

        codec.Decompress(sourceArea.AsSpan(10, compressed.Length), destinationArea.AsSpan(10, data.Length));

        await Assert.That(destinationArea[..10].All(b => b == Sentinel)).IsTrue();
        await Assert.That(destinationArea[(10 + data.Length)..].All(b => b == Sentinel)).IsTrue();
        await Assert.That(destinationArea.AsSpan(10, data.Length).SequenceEqual(data)).IsTrue();
    }

    [Test]
    public async Task ASmallPageAfterABigOne_IsNotAffectedByWhatThePooledArrayHeld()
    {
        var codec = new GzipCodec();
        var big = CodecTestData.Cases["mixed"];
        var small = CodecTestData.Cases["single"];

        for (int round = 0; round < 3; round++)
        {
            var bigBack = CodecTestData.Decompress(codec, CodecTestData.Compress(codec, big), big.Length);
            var smallBack = CodecTestData.Decompress(codec, CodecTestData.Compress(codec, small), small.Length);

            await Assert.That(bigBack.AsSpan().SequenceEqual(big)).IsTrue();
            await Assert.That(smallBack.AsSpan().SequenceEqual(small)).IsTrue();
        }
    }

    [Test]
    public async Task ManyThreads_CompressAndDecompressDifferentPagesTogether()
    {
        var codec = new GzipCodec(CompressionLevel.Fastest);

        var results = await Task.WhenAll(Enumerable.Range(0, 24).Select(index => Task.Run(() =>
        {
            var page = CodecTestData.Lcg(1000 + (index * 7919 % 90_000), (uint)index);
            for (int i = 0; i < page.Length; i += 3)
            {
                page[i] = (byte)(i % 11);
            }

            var restored = CodecTestData.Decompress(codec, CodecTestData.Compress(codec, page), page.Length);
            return restored.AsSpan().SequenceEqual(page);
        })));

        await Assert.That(results.All(ok => ok)).IsTrue();
    }

    [Test]
    public async Task TheStreamsAreOverArrays_SoAPageOfAnySizeIsHandled()
    {
        var codec = new GzipCodec();

        foreach (int size in new[] { 1, 2, 100, 4095, 4096, 4097, 65_536, 1_000_003 })
        {
            var page = CodecTestData.Lcg(size, (uint)size);

            var restored = CodecTestData.Decompress(codec, CodecTestData.Compress(codec, page), size);

            await Assert.That(restored.AsSpan().SequenceEqual(page)).IsTrue().Because($"size {size}");
        }
    }
}
