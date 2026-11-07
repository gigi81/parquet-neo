using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using System.Runtime.InteropServices;
using ParquetNeo.Encodings;

namespace ParquetNeo.Core;

/// <summary>Collects the distinct values of a column chunk, in order of first appearance, for dictionary encoding.</summary>
internal abstract class DictionaryBuilder
{
    /// <summary>The number of distinct values so far.</summary>
    public abstract int Count { get; }

    /// <summary>The size in bytes the dictionary page body would have (the entries encoded PLAIN).</summary>
    public abstract long EncodedSize { get; }

    /// <summary>Writes the entries, PLAIN encoded and in index order, which is the body of the dictionary page.</summary>
    public abstract void WritePlain(IBufferWriter<byte> output);
}

internal delegate void PlainWriter<T>(ReadOnlySpan<T> values, IBufferWriter<byte> output);

/// <summary>
/// Dictionary for 4- and 8-byte values. Floating-point values are added as their bit patterns, so that -0.0 and
/// +0.0 stay distinct and every NaN payload survives; the PLAIN bytes are the same either way.
/// </summary>
internal sealed class FixedDictionary<T> : DictionaryBuilder where T : unmanaged, IEquatable<T>
{
    private readonly Dictionary<T, int> _indexes = new();
    private readonly List<T> _entries = new();
    private readonly PlainWriter<T> _writePlain;

    public FixedDictionary(PlainWriter<T> writePlain)
    {
        _writePlain = writePlain;
    }

    public override int Count => _entries.Count;

    public override long EncodedSize => (long)_entries.Count * System.Runtime.CompilerServices.Unsafe.SizeOf<T>();

    /// <summary>Adds the values, appending the dictionary index of each one to <paramref name="indexes"/>.</summary>
    public void Add(ReadOnlySpan<T> values, ArrayBufferWriter<int> indexes)
    {
        var output = indexes.GetSpan(values.Length);
        for (int i = 0; i < values.Length; i++)
        {
            ref int slot = ref CollectionsMarshal.GetValueRefOrAddDefault(_indexes, values[i], out bool exists);
            if (!exists)
            {
                slot = _entries.Count;
                _entries.Add(values[i]);
            }

            output[i] = slot;
        }

        indexes.Advance(values.Length);
    }

    public override void WritePlain(IBufferWriter<byte> output) => _writePlain(CollectionsMarshal.AsSpan(_entries), output);
}

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
