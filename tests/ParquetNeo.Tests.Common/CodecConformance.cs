using ParquetNeo.Compression;
using ParquetNeo.Exceptions;

namespace ParquetNeo.Tests.Compression;

/// <summary>
/// What every <see cref="ICodec"/> must do, as test bodies that the test project of each codec runs for its own codecs: the library
/// of the built-in codecs, and each add-on package. The tests that are about one codec only stay in the project of that codec.
/// </summary>
public static class CodecConformance
{
    public static async Task EveryCase_RoundTrips_WithinTheMaximumLength(IEnumerable<(string Name, ICodec Codec)> codecs)
    {
        foreach (var (codecName, codec) in codecs)
        {
            foreach (var (caseName, original) in CodecTestData.Cases)
            {
                var compressed = CodecTestData.Compress(codec, original);
                var decompressed = CodecTestData.Decompress(codec, compressed, original.Length);

                await Assert.That(compressed.Length).IsLessThanOrEqualTo(codec.GetMaxCompressedLength(original.Length))
                    .Because($"{codecName}/{caseName} size bound");
                await Assert.That(decompressed.AsSpan().SequenceEqual(original)).IsTrue().Because($"{codecName}/{caseName}");
            }
        }
    }

    /// <summary>For codecs that compress: the repetitive cases come out at less than half their size.</summary>
    public static async Task CompressibleData_ReallyShrinks(IEnumerable<(string Name, ICodec Codec)> codecs)
    {
        foreach (var (codecName, codec) in codecs)
        {
            foreach (string caseName in new[] { "run", "text", "mixed" })
            {
                var original = CodecTestData.Cases[caseName];
                var compressed = CodecTestData.Compress(codec, original);

                await Assert.That(compressed.Length).IsLessThan(original.Length / 2).Because($"{codecName}/{caseName}");
            }
        }
    }

    public static async Task IncompressibleData_StaysWithinTheMaximumLength(IEnumerable<(string Name, ICodec Codec)> codecs)
    {
        var noise = CodecTestData.Lcg(1 << 20, 99); // 1 MB, several Snappy blocks

        foreach (var (codecName, codec) in codecs)
        {
            var compressed = CodecTestData.Compress(codec, noise);

            await Assert.That(compressed.Length).IsLessThanOrEqualTo(codec.GetMaxCompressedLength(noise.Length)).Because(codecName);
            await Assert.That(CodecTestData.Decompress(codec, compressed, noise.Length).AsSpan().SequenceEqual(noise))
                .IsTrue().Because(codecName);
        }
    }

    /// <summary>Other readers reject a zero-byte compressed page for a codec that frames its data.</summary>
    public static async Task EmptyInput_ProducesAValidNonEmptyStream(IEnumerable<(string Name, ICodec Codec)> codecs)
    {
        foreach (var (name, codec) in codecs)
        {
            var compressed = CodecTestData.Compress(codec, []);

            await Assert.That(compressed.Length).IsGreaterThan(0).Because(name);
        }
    }

    public static async Task InputsAroundBlockBoundaries_RoundTrip(IEnumerable<(string Name, ICodec Codec)> codecs)
    {
        // 64 KB is Snappy's block size; the small sizes cover the "too short to match" threshold.
        int[] sizes = [1, 2, 3, 4, 5, 14, 15, 16, 17, 31, 32, 33, 255, 256, 257, 65535, 65536, 65537, 131071, 131072, 131073];
        foreach (var (codecName, codec) in codecs)
        {
            foreach (int size in sizes)
            {
                // Half repetitive, half noisy, so both literals and long matches occur.
                var data = new byte[size];
                var noise = CodecTestData.Lcg(size, (uint)size);
                for (int i = 0; i < size; i++)
                {
                    data[i] = (i / 97) % 2 == 0 ? (byte)(i % 13) : noise[i];
                }

                var restored = CodecTestData.Decompress(codec, CodecTestData.Compress(codec, data), size);

                await Assert.That(restored.AsSpan().SequenceEqual(data)).IsTrue().Because($"{codecName}/{size}");
            }
        }
    }

    public static async Task RandomStructuredInputs_RoundTrip(IEnumerable<(string Name, ICodec Codec)> codecs)
    {
        var random = new Random(77);
        foreach (var (codecName, codec) in codecs)
        {
            for (int attempt = 0; attempt < 60; attempt++)
            {
                var data = RandomStructured(random, random.Next(0, 90_000));

                var restored = CodecTestData.Decompress(codec, CodecTestData.Compress(codec, data), data.Length);

                await Assert.That(restored.AsSpan().SequenceEqual(data)).IsTrue().Because($"{codecName} attempt {attempt}");
            }
        }
    }

    public static async Task ReportsItsParquetId(ICodec codec, CompressionCodec expected)
    {
        await Assert.That(codec.Id).IsEqualTo(expected);
    }

    public static async Task Compress_RejectsADestinationThatMightBeTooSmall(ICodec codec)
    {
        await Assert.That(() => codec.Compress(new byte[100], new byte[10])).Throws<ArgumentException>();
    }

    /// <summary>Data compressed by an independent implementation (pyarrow, Python's gzip module), from the Fixtures/Codecs folder of the test project that runs.</summary>
    public static async Task IndependentlyCompressedData_DecompressesToTheOriginal(ICodec codec, string extension)
    {
        foreach (var (name, original) in CodecTestData.Cases)
        {
            var decompressed = CodecTestData.Decompress(codec, CodecTestData.Vector(name, extension), original.Length);

            await Assert.That(decompressed.AsSpan().SequenceEqual(original)).IsTrue().Because($"{extension}/{name}");
        }
    }

    // ---- whatever the input, decompression either succeeds or throws CompressionException ----

    /// <param name="lastBytesMayBeMissing">
    /// True for a format whose decoder stops at the end of the compressed stream and never looks at what follows (the BCL gzip inflater
    /// does not read the 8-byte trailer), so that losing only the last byte leaves complete data, which is accepted.
    /// </param>
    public static async Task TruncatedData_IsRejected(IEnumerable<(string Name, ICodec Codec)> codecs, bool lastBytesMayBeMissing = false)
    {
        var original = CodecTestData.Cases["mixed"];
        foreach (var (name, codec) in codecs)
        {
            var compressed = CodecTestData.Compress(codec, original);

            int[] lengths = lastBytesMayBeMissing
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

    public static async Task WrongDestinationSize_IsRejected_InBothDirections(IEnumerable<(string Name, ICodec Codec)> codecs)
    {
        var original = CodecTestData.Cases["text"];
        foreach (var (name, codec) in codecs)
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

    public static async Task EmptyPages_AreAcceptedWhereAnEmptyStreamIsValid(IEnumerable<(string Name, ICodec Codec)> codecs)
    {
        foreach (var (name, codec) in codecs)
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

    public static async Task GarbageInput_OnlyEverFailsWithCompressionException(IEnumerable<(string Name, ICodec Codec)> codecs)
    {
        foreach (var (name, codec) in codecs)
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

    public static async Task CorruptedValidData_OnlyEverFailsWithCompressionException(IEnumerable<(string Name, ICodec Codec)> codecs)
    {
        var original = CodecTestData.Cases["mixed"][..20_000];
        foreach (var (name, codec) in codecs)
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
    public static string? Probe(Action action)
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

    private static byte[] RandomStructured(Random random, int length)
    {
        var data = new List<byte>(length);
        while (data.Count < length)
        {
            int run = Math.Min(length - data.Count, random.Next(1, 600));
            switch (random.Next(4))
            {
                case 0: // a single repeated byte
                    data.AddRange(Enumerable.Repeat((byte)random.Next(256), run));
                    break;
                case 1: // noise
                    data.AddRange(Enumerable.Range(0, run).Select(_ => (byte)random.Next(256)));
                    break;
                case 2 when data.Count > 8: // a copy of earlier data, possibly overlapping itself
                    int from = random.Next(0, data.Count);
                    for (int i = 0; i < run; i++)
                    {
                        data.Add(data[from + i % Math.Max(1, data.Count - from)]);
                    }

                    break;
                default: // a short repeating pattern
                    var pattern = Enumerable.Range(0, random.Next(2, 9)).Select(_ => (byte)random.Next(256)).ToArray();
                    data.AddRange(Enumerable.Range(0, run).Select(i => pattern[i % pattern.Length]));
                    break;
            }
        }

        return data.ToArray();
    }
}
