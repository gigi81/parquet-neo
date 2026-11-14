using System.Buffers.Binary;
using ParquetNeo.Encodings;

namespace ParquetNeo.Core;

/// <summary>
/// A split block bloom filter, as the Parquet format defines it: the bits are blocks of 256 (eight 32-bit words), a value
/// is hashed to 64 bits (xxHash64 of its plain encoding), the upper half picks the block and the lower half sets or tests one
/// bit in each of the eight words.
/// </summary>
internal sealed class BloomFilter
{
    private const int WordsPerBlock = 8;
    private const int BytesPerBlock = WordsPerBlock * sizeof(uint);

    private static readonly uint[] Salt = [0x47b6137b, 0x44974d91, 0x8824ad5b, 0xa2b7289d, 0x705495c7, 0x2df1424b, 0x9efc4947, 0x5c6bfb31];

    private readonly uint[] _words;

    private BloomFilter(uint[] words) => _words = words;

    /// <summary>The size of the bits in bytes.</summary>
    public int ByteCount => _words.Length * sizeof(uint);

    /// <summary>An empty filter of at least <paramref name="byteCount"/> bytes, rounded up to a whole number of blocks.</summary>
    public static BloomFilter Create(int byteCount)
    {
        int blocks = Math.Max(1, (int)(((long)byteCount + BytesPerBlock - 1) / BytesPerBlock));
        return new BloomFilter(new uint[blocks * WordsPerBlock]);
    }

    /// <summary>
    /// The size, in bytes, of a filter for <paramref name="distinct"/> values that is wrong about a value that is not there with
    /// the chance <paramref name="falsePositiveRate"/>: a power of two, at least one block and at most <paramref name="maxBytes"/>.
    /// </summary>
    public static int OptimalByteCount(long distinct, double falsePositiveRate, int maxBytes)
    {
        double bits = -8.0 * Math.Max(distinct, 1) / Math.Log(1 - Math.Pow(falsePositiveRate, 1.0 / 8));
        long bytes = BytesPerBlock;
        while (bytes * 8 < bits && bytes < maxBytes)
        {
            bytes *= 2;
        }

        return (int)Math.Max(BytesPerBlock, Math.Min(bytes, Math.Max(maxBytes, BytesPerBlock)));
    }

    /// <summary>A filter from its bits, or null when their number is not a whole number of blocks.</summary>
    public static BloomFilter? FromBytes(ReadOnlySpan<byte> bits)
    {
        if (bits.Length == 0 || bits.Length % BytesPerBlock != 0)
        {
            return null;
        }

        var words = new uint[bits.Length / sizeof(uint)];
        for (int i = 0; i < words.Length; i++)
        {
            words[i] = BinaryPrimitives.ReadUInt32LittleEndian(bits[(i * sizeof(uint))..]);
        }

        return new BloomFilter(words);
    }

    /// <summary>The hash a value is looked up by: of its plain encoding (a byte array without its length).</summary>
    public static ulong HashOf(ReadOnlySpan<byte> plainValue) => XxHash64.Hash(plainValue);

    public void Insert(ulong hash)
    {
        int block = BlockOf(hash) * WordsPerBlock;
        uint key = (uint)hash;
        for (int i = 0; i < WordsPerBlock; i++)
        {
            _words[block + i] |= 1u << (int)((key * Salt[i]) >> 27);
        }
    }

    /// <summary>False when the value was certainly not inserted; true when it may have been.</summary>
    public bool MightContain(ulong hash)
    {
        int block = BlockOf(hash) * WordsPerBlock;
        uint key = (uint)hash;
        for (int i = 0; i < WordsPerBlock; i++)
        {
            if ((_words[block + i] & (1u << (int)((key * Salt[i]) >> 27))) == 0)
            {
                return false;
            }
        }

        return true;
    }

    public void CopyTo(Span<byte> destination)
    {
        for (int i = 0; i < _words.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(destination[(i * sizeof(uint))..], _words[i]);
        }
    }

    private int BlockOf(ulong hash) => (int)(((hash >> 32) * (ulong)(_words.Length / WordsPerBlock)) >> 32);
}
