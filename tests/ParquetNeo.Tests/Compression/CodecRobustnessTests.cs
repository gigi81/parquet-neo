using ParquetNeo.Compression;

namespace ParquetNeo.Tests.Compression;

/// <summary>Whatever the input, decompression either succeeds or throws <see cref="ParquetFormatException"/>.</summary>
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
                    .Throws<ParquetFormatException>().Because($"{name} truncated to {keep} of {compressed.Length}");
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
                .Throws<ParquetFormatException>().Because($"{name} destination one byte short");
            await Assert.That(() => codec.Decompress(compressed, new byte[original.Length + 1]))
                .Throws<ParquetFormatException>().Because($"{name} destination one byte long");
            await Assert.That(() => codec.Decompress(compressed, new byte[original.Length * 4]))
                .Throws<ParquetFormatException>().Because($"{name} destination far too long");
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
            await Assert.That(() => codec.Decompress([], new byte[10])).Throws<ParquetFormatException>().Because(name);
        }
    }

    [Test]
    public async Task GarbageInput_OnlyEverFailsWithParquetFormatException()
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
    public async Task CorruptedValidData_OnlyEverFailsWithParquetFormatException()
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
        catch (ParquetFormatException)
        {
            return null;
        }
        catch (Exception exception)
        {
            return $"{exception.GetType().Name}: {exception.Message}";
        }
    }
}
