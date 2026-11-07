using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using ParquetNeo.Encodings;

namespace ParquetNeo.Core;

/// <summary>Dictionary for BYTE_ARRAY and FIXED_LEN_BYTE_ARRAY values, looked up by span so a hit allocates nothing.</summary>
internal sealed class BinaryDictionary : DictionaryBuilder
{
    private readonly Dictionary<byte[], int> _indexes;
    private readonly Dictionary<byte[], int>.AlternateLookup<ReadOnlySpan<byte>> _lookup;
    private readonly List<byte[]> _entries = new();
    private readonly bool _lengthPrefixed;
    private long _encodedSize;

    /// <param name="lengthPrefixed">True for BYTE_ARRAY (each entry carries a 4-byte length), false for FIXED_LEN_BYTE_ARRAY.</param>
    public BinaryDictionary(bool lengthPrefixed)
    {
        _lengthPrefixed = lengthPrefixed;
        _indexes = new Dictionary<byte[], int>(new ByteArrayComparer());
        _lookup = _indexes.GetAlternateLookup<ReadOnlySpan<byte>>();
    }

    public override int Count => _entries.Count;

    public override long EncodedSize => _encodedSize;

    /// <summary>Returns the index of <paramref name="value"/>, adding it (as a copy) if it is new.</summary>
    public int GetOrAdd(ReadOnlySpan<byte> value)
    {
        if (_lookup.TryGetValue(value, out int index))
        {
            return index;
        }

        var copy = value.ToArray();
        index = _entries.Count;
        _entries.Add(copy);
        _indexes.Add(copy, index);
        _encodedSize += value.Length + (_lengthPrefixed ? sizeof(int) : 0);
        return index;
    }

    public override void WritePlain(IBufferWriter<byte> output)
    {
        foreach (var entry in _entries)
        {
            if (_lengthPrefixed)
            {
                PlainEncoder.WriteByteArray(entry, output);
            }
            else
            {
                output.Write(entry);
            }
        }
    }

    /// <summary>
    /// Compares and hashes byte sequences. The hash is a fast multiply-and-rotate over 8 bytes at a time, seeded
    /// randomly per dictionary so that input chosen by an attacker cannot be crafted to collide (the seed has no
    /// effect on the output: entries are numbered by first appearance, not by hash).
    /// </summary>
    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>, IAlternateEqualityComparer<ReadOnlySpan<byte>, byte[]>
    {
        private const ulong Multiplier = 0x9E3779B97F4A7C15UL;
        private readonly ulong _seed = (ulong)Random.Shared.NextInt64();

        public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);

        public int GetHashCode(byte[] obj) => Hash(obj);

        public bool Equals(ReadOnlySpan<byte> alternate, byte[] other) => alternate.SequenceEqual(other);

        public int GetHashCode(ReadOnlySpan<byte> alternate) => Hash(alternate);

        public byte[] Create(ReadOnlySpan<byte> alternate) => alternate.ToArray();

        private int Hash(ReadOnlySpan<byte> bytes)
        {
            ulong hash = _seed ^ ((ulong)bytes.Length * Multiplier);
            while (bytes.Length >= sizeof(ulong))
            {
                hash = (BitOperations.RotateLeft(hash, 23) ^ BinaryPrimitives.ReadUInt64LittleEndian(bytes)) * Multiplier;
                bytes = bytes[sizeof(ulong)..];
            }

            if (bytes.Length > 0)
            {
                // 1 to 7 bytes left: read them into one word (zero padded; the length is already mixed in).
                ulong tail = 0;
                for (int i = 0; i < bytes.Length; i++)
                {
                    tail |= (ulong)bytes[i] << (8 * i);
                }

                hash = (BitOperations.RotateLeft(hash, 23) ^ tail) * Multiplier;
            }

            hash ^= hash >> 32;
            return (int)hash;
        }
    }
}
