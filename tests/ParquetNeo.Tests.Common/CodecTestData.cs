using ParquetNeo.Compression;

namespace ParquetNeo.Tests.Compression;

/// <summary>
/// The inputs of the compression vectors, rebuilt from formulas that mirror
/// tests/fixtures/generate_codec_fixtures.py. The vectors themselves are in the Fixtures/Codecs folder of the test project of each codec.
/// </summary>
public static class CodecTestData
{
    public static IReadOnlyDictionary<string, byte[]> Cases { get; } = new Dictionary<string, byte[]>
    {
        ["empty"] = [],
        ["single"] = "a"u8.ToArray(),
        ["run"] = Enumerable.Repeat((byte)'a', 1000).ToArray(),
        ["text"] = Enumerable.Range(0, 200).SelectMany(_ => "hello parquet "u8.ToArray()).ToArray(),
        ["ramp"] = Enumerable.Range(0, 5000).Select(i => (byte)(i % 256)).ToArray(),
        ["noise"] = Lcg(5000, 12345),
        ["mixed"] = Mixed(140_000),
    };

    public static byte[] Lcg(int count, uint seed)
    {
        ulong x = seed;
        var bytes = new byte[count];
        for (int i = 0; i < count; i++)
        {
            x = (x * 1103515245UL + 12345UL) & 0x7FFFFFFFUL;
            bytes[i] = (byte)((x >> 16) & 0xFF);
        }

        return bytes;
    }

    /// <summary>The bytes a reference implementation compressed a case to, from the Fixtures/Codecs folder of the test project that runs.</summary>
    public static byte[] Vector(string name, string extension) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Codecs", $"{name}.{extension}"));

    public static byte[] Compress(ICodec codec, byte[] data)
    {
        var buffer = new byte[codec.GetMaxCompressedLength(data.Length)];
        int written = codec.Compress(data, buffer);
        return buffer[..written];
    }

    public static byte[] Decompress(ICodec codec, byte[] compressed, int length)
    {
        var data = new byte[length];
        codec.Decompress(compressed, data);
        return data;
    }

    /// <summary>1000-byte blocks: every 5th is noise (seeded by its index), the others repeat a sentence.</summary>
    private static byte[] Mixed(int count)
    {
        var sentence = "the quick brown fox jumps over the lazy dog "u8.ToArray();
        var output = new List<byte>(count + 1000);
        for (int block = 0; output.Count < count; block++)
        {
            if (block % 5 == 0)
            {
                output.AddRange(Lcg(1000, (uint)(1000 + block)));
            }
            else
            {
                output.AddRange(Enumerable.Range(0, 1000).Select(i => sentence[i % sentence.Length]));
            }
        }

        return output.Take(count).ToArray();
    }
}
