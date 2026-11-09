using ParquetNeo.Exceptions;

namespace ParquetNeo.Tests.Compression;

/// <summary>Whatever the input, decompression either succeeds or throws <see cref="CompressionException"/>.</summary>
public class CodecRobustnessTests
{
    private static readonly string[] Real = ["snappy", "gzip", "brotli", "zstd", "lz4raw"];

    [Test]
    public async Task TruncatedData_IsRejected()
    {
        var original = CodecTestData.Cases["mixed"];
        foreach (var (name, codec) in CodecTestData.Codecs.Where(c => Real.Contains(c.Name)))
        {
            var compressed = CodecTestData.Compress(codec, original);

            // The BCL gzip inflater stops at the end of the deflate stream and never looks at the 8-byte trailer,
            // so losing only the last byte(s) of a gzip member leaves complete data and is accepted.
            int[] lengths = name.StartsWith("gzip", StringComparison.Ordinal)
                ? [0, 1, compressed.Length / 4, compressed.Length / 2]
                : [0, 1, compressed.Length / 4, compressed.Length / 2, compressed.Length - 1];

            foreach (int keep in lengths)
            {
                var truncated = compressed[..keep];

                await Assert.That(() => codec.Decompress(truncated, new byte[original.Length]))
                    .Throws<CompressionException>().Because($"{name} truncated to {keep} of {compressed.Length}");
            }
        }
    }

    [Test]
    public async Task WrongDestinationSize_IsRejected_InBothDirections()
    {
        var original = CodecTestData.Cases["text"];
        foreach (var (name, codec) in CodecTestData.Codecs.Where(c => Real.Contains(c.Name)))
        {
            var compressed = CodecTestData.Compress(codec, original);

            await Assert.That(() => codec.Decompress(compressed, new byte[original.Length - 1]))
                .Throws<CompressionException>().Because($"{name} destination one byte short");
            await Assert.That(() => codec.Decompress(compressed, new byte[original.Length + 1]))
                .Throws<CompressionException>().Because($"{name} destination one byte long");
            await Assert.That(() => codec.Decompress(compressed, new byte[original.Length * 4]))
                .Throws<CompressionException>().Because($"{name} destination far too long");
        }
    }

    [Test]
    public async Task EmptyPages_AreAcceptedWhereAnEmptyStreamIsValid()
    {
        foreach (var (name, codec) in CodecTestData.Codecs.Where(c => Real.Contains(c.Name)))
        {
            // A page with no data, as our own writers (and the reference ones) produce it.
            var compressed = CodecTestData.Compress(codec, []);
            CodecTestData.Decompress(codec, compressed, 0);

            await Assert.That(CodecTestData.Decompress(codec, compressed, 0).Length).IsEqualTo(0).Because(name);
            // No bytes at all for no data is tolerated; no bytes for real data never is.
            codec.Decompress([], []);
            await Assert.That(() => codec.Decompress([], new byte[10])).Throws<CompressionException>().Because(name);
        }
    }

    [Test]
    public async Task GarbageInput_OnlyEverFailsWithCompressionException()
    {
        foreach (var (name, codec) in CodecTestData.Codecs.Where(c => Real.Contains(c.Name)))
        {
            var random = new Random(5);
            for (int attempt = 0; attempt < 400; attempt++)
            {
                var garbage = new byte[random.Next(1, 200)];
                random.NextBytes(garbage);

                await Assert.That(Probe(() => codec.Decompress(garbage, new byte[random.Next(0, 300)]))).IsNull().Because(name);
            }
        }
    }

    [Test]
    public async Task SnappyStreamsWithACorrectPreamble_AndGarbageBodies_OnlyFailWithCompressionException()
    {
        // Random garbage almost never declares the destination's length, so it is rejected before decoding.
        // Declare it correctly so the element decoder itself sees hostile bytes.
        var codec = CodecTestData.Codecs.First(c => c.Name == "snappy").Codec;
        var random = new Random(17);
        for (int attempt = 0; attempt < 5000; attempt++)
        {
            int length = random.Next(0, 400);
            var body = new byte[random.Next(0, 150)];
            random.NextBytes(body);
            var preamble = length < 128 ? new[] { (byte)length } : new[] { (byte)(length | 0x80), (byte)(length >> 7) };
            var input = preamble.Concat(body).ToArray();

            await Assert.That(Probe(() => codec.Decompress(input, new byte[length]))).IsNull().Because($"attempt {attempt}");
        }
    }

    [Test]
    public async Task CorruptedValidData_OnlyEverFailsWithCompressionException()
    {
        var original = CodecTestData.Cases["mixed"][..20_000];
        foreach (var (name, codec) in CodecTestData.Codecs.Where(c => Real.Contains(c.Name)))
        {
            var compressed = CodecTestData.Compress(codec, original);
            var random = new Random(9);
            for (int attempt = 0; attempt < 300; attempt++)
            {
                var corrupted = (byte[])compressed.Clone();
                for (int flips = random.Next(1, 4); flips > 0; flips--)
                {
                    corrupted[random.Next(corrupted.Length)] ^= (byte)(1 << random.Next(8));
                }

                // Formats without checksums may decode to different data; that is fine, crashing is not.
                await Assert.That(Probe(() => codec.Decompress(corrupted, new byte[original.Length]))).IsNull().Because(name);
            }
        }
    }

    /// <summary>Returns null if the call succeeds or throws the expected exception, otherwise a description.</summary>
    private static string? Probe(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (CompressionException)
        {
            return null;
        }
        catch (Exception exception)
        {
            return $"{exception.GetType().Name}: {exception.Message}";
        }
    }
}
