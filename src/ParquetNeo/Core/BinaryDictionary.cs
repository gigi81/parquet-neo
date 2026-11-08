using System.Buffers;
using System.Buffers.Binary;
using System.Numerics;
using ParquetNeo.Encodings;

namespace ParquetNeo.Core;

/// <summary>Dictionary for BYTE_ARRAY and FIXED_LEN_BYTE_ARRAY values, looked up by span so a hit allocates nothing.</summary>
/// <remarks>
/// An open-addressing table over arrays from the pool (given back with <see cref="Dispose"/>): the values are copied one after the
/// other into a single byte array, and an entry is its offset, its length and its hash, so a dictionary of any number of values
/// is a handful of arrays and a mismatch is usually told from the hashes alone. Entries are numbered in order of first appearance,
/// which is what the file holds. The hash is seeded randomly for every dictionary, so that input chosen by an attacker cannot be
/// crafted to collide (the seed has no effect on the output).
/// </remarks>
internal sealed class BinaryDictionary : IDictionaryBuilder
{
    private const int InitialTableSize = 1024;
    private const ulong Multiplier = 0x9E3779B97F4A7C15UL;

    private readonly bool _lengthPrefixed;
    private readonly ulong _seed = (ulong)Random.Shared.NextInt64();

    private byte[] _arena = ArrayPool<byte>.Shared.Rent(4096);
    private int _arenaLength;
    private int[] _offsets = ArrayPool<int>.Shared.Rent(InitialTableSize / 2);
    private int[] _lengths = ArrayPool<int>.Shared.Rent(InitialTableSize / 2);
    private uint[] _hashes = ArrayPool<uint>.Shared.Rent(InitialTableSize / 2);
    private int[] _table = NewTable(InitialTableSize);
    private int _tableSize = InitialTableSize;
    private int _count;
    private long _encodedSize;

    /// <param name="lengthPrefixed">True for BYTE_ARRAY (each entry carries a 4-byte length), false for FIXED_LEN_BYTE_ARRAY.</param>
    public BinaryDictionary(bool lengthPrefixed)
    {
        _lengthPrefixed = lengthPrefixed;
    }

    public int Count => _count;

    public long EncodedSize => _encodedSize;

    /// <summary>Returns the index of <paramref name="value"/>, adding it (as a copy) if it is new, which <paramref name="added"/> says.</summary>
    public int GetOrAdd(ReadOnlySpan<byte> value, out bool added)
    {
        uint hash = Hash(value);
        int mask = _tableSize - 1;
        int slot = (int)hash & mask;
        while (true)
        {
            int stored = _table[slot];
            if (stored == 0)
            {
                break;
            }

            int entry = stored - 1;
            if (_hashes[entry] == hash && _lengths[entry] == value.Length && value.SequenceEqual(_arena.AsSpan(_offsets[entry], value.Length)))
            {
                added = false;
                return entry;
            }

            slot = (slot + 1) & mask;
        }

        added = true;
        Append(value, hash);
        _table[slot] = _count;
        if (_count * 2 > _tableSize)
        {
            Grow();
        }

        _encodedSize += value.Length + (_lengthPrefixed ? sizeof(int) : 0);
        return _count - 1;
    }

    public void WritePlain(IBufferWriter<byte> output)
    {
        for (int entry = 0; entry < _count; entry++)
        {
            var bytes = _arena.AsSpan(_offsets[entry], _lengths[entry]);
            if (_lengthPrefixed)
            {
                PlainEncoder.WriteByteArray(bytes, output);
            }
            else
            {
                output.Write(bytes);
            }
        }
    }

    /// <summary>Gives the arrays back to the pool; the dictionary cannot be used after this.</summary>
    public void Dispose()
    {
        if (_count < 0)
        {
            return;
        }

        ArrayPool<byte>.Shared.Return(_arena);
        ArrayPool<int>.Shared.Return(_offsets);
        ArrayPool<int>.Shared.Return(_lengths);
        ArrayPool<uint>.Shared.Return(_hashes);
        ArrayPool<int>.Shared.Return(_table);
        _arena = [];
        _offsets = _lengths = _table = [];
        _hashes = [];
        _count = -1;
    }

    /// <summary>Copies the value into the arena and records it as the next entry.</summary>
    private void Append(ReadOnlySpan<byte> value, uint hash)
    {
        if (_count == _offsets.Length)
        {
            _offsets = Grown(_offsets, _count);
            _lengths = Grown(_lengths, _count);
            _hashes = Grown(_hashes, _count);
        }

        if (_arenaLength + value.Length > _arena.Length)
        {
            long wanted = Math.Max((long)_arenaLength + value.Length, (long)_arena.Length * 2);
            var larger = ArrayPool<byte>.Shared.Rent((int)Math.Min(wanted, Array.MaxLength));
            _arena.AsSpan(0, _arenaLength).CopyTo(larger);
            ArrayPool<byte>.Shared.Return(_arena);
            _arena = larger;
        }

        value.CopyTo(_arena.AsSpan(_arenaLength));
        _offsets[_count] = _arenaLength;
        _lengths[_count] = value.Length;
        _hashes[_count] = hash;
        _arenaLength += value.Length;
        _count++;
    }

    private static T[] Grown<T>(T[] array, int used) where T : unmanaged
    {
        var larger = ArrayPool<T>.Shared.Rent(array.Length * 2);
        array.AsSpan(0, used).CopyTo(larger);
        ArrayPool<T>.Shared.Return(array);
        return larger;
    }

    /// <summary>Doubles the table, so that it is never more than half full.</summary>
    private void Grow()
    {
        int size = _tableSize * 2;
        var table = NewTable(size);
        int mask = size - 1;
        for (int entry = 0; entry < _count; entry++)
        {
            int slot = (int)_hashes[entry] & mask;
            while (table[slot] != 0)
            {
                slot = (slot + 1) & mask;
            }

            table[slot] = entry + 1;
        }

        ArrayPool<int>.Shared.Return(_table);
        _table = table;
        _tableSize = size;
    }

    /// <summary>A table of exactly <paramref name="size"/> empty slots (the pool may hand out an array that is bigger and dirty).</summary>
    private static int[] NewTable(int size)
    {
        var table = ArrayPool<int>.Shared.Rent(size);
        Array.Clear(table, 0, size);
        return table;
    }

    /// <summary>A fast multiply-and-rotate hash over 8 bytes at a time, seeded per dictionary.</summary>
    private uint Hash(ReadOnlySpan<byte> bytes)
    {
        int length = bytes.Length;
        ulong hash = _seed ^ ((ulong)length * Multiplier);
        int position = 0;
        while (length - position >= sizeof(ulong))
        {
            hash = (BitOperations.RotateLeft(hash, 23) ^ BinaryPrimitives.ReadUInt64LittleEndian(bytes[position..])) * Multiplier;
            position += sizeof(ulong);
        }

        if (position < length)
        {
            // The 1 to 7 bytes left, read as one word: the last 8 bytes again when there are that many, else two overlapping words.
            ulong tail;
            if (length >= sizeof(ulong))
            {
                tail = BinaryPrimitives.ReadUInt64LittleEndian(bytes[^sizeof(ulong)..]);
            }
            else if (length >= sizeof(uint))
            {
                tail = BinaryPrimitives.ReadUInt32LittleEndian(bytes) | ((ulong)BinaryPrimitives.ReadUInt32LittleEndian(bytes[^sizeof(uint)..]) << 32);
            }
            else
            {
                tail = bytes[0] | ((ulong)bytes[length >> 1] << 8) | ((ulong)bytes[^1] << 16);
            }

            hash = (BitOperations.RotateLeft(hash, 23) ^ tail) * Multiplier;
        }

        hash ^= hash >> 32;
        hash *= Multiplier;
        return (uint)(hash >> 32);
    }
}
