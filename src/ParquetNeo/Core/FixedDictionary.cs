using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace ParquetNeo.Core;

/// <summary>
/// Dictionary for 4- and 8-byte values. Floating-point values are added as their bit patterns, so that -0.0 and
/// +0.0 stay distinct and every NaN payload survives; the PLAIN bytes are the same either way.
/// </summary>
/// <remarks>
/// An open-addressing table over arrays taken from the pool and given back with <see cref="Dispose"/>, so that building a
/// dictionary of a few hundred thousand entries (a column of identifiers, until the writer gives the dictionary up) allocates
/// next to nothing. Entries are numbered in order of first appearance, which is what the file holds. The multiplier of the hash
/// is random for every dictionary, so that input chosen by an attacker cannot be crafted to collide.
/// </remarks>
internal sealed class FixedDictionary<T> : IDictionaryBuilder where T : unmanaged, IBinaryInteger<T>
{
    private const int InitialTableSize = 1024;

    private readonly PlainWriter<T> _writePlain;
    private readonly ulong _multiplier = (ulong)Random.Shared.NextInt64() | 1UL;

    private T[] _entries = ArrayPool<T>.Shared.Rent(InitialTableSize / 2);
    private int[] _table = NewTable(InitialTableSize);
    private int _tableSize = InitialTableSize;
    private int _shift = 64 - BitOperations.Log2(InitialTableSize);
    private int _count;

    public FixedDictionary(PlainWriter<T> writePlain)
    {
        _writePlain = writePlain;
    }

    public int Count => _count;

    public long EncodedSize => (long)_count * Unsafe.SizeOf<T>();

    /// <summary>Adds the values, appending the dictionary index of each one to <paramref name="indexes"/>.</summary>
    public void Add(ReadOnlySpan<T> values, PooledBufferWriter<int> indexes)
    {
        var output = indexes.GetSpan(values.Length);
        for (int i = 0; i < values.Length; i++)
        {
            output[i] = GetOrAdd(values[i]);
        }

        indexes.Advance(values.Length);
    }

    public void WritePlain(IBufferWriter<byte> output) => _writePlain(_entries.AsSpan(0, _count), output);

    /// <summary>Gives the arrays back to the pool; the dictionary cannot be used after this.</summary>
    public void Dispose()
    {
        if (_count < 0)
        {
            return;
        }

        ArrayPool<T>.Shared.Return(_entries);
        ArrayPool<int>.Shared.Return(_table);
        _entries = [];
        _table = [];
        _count = -1;
    }

    private int GetOrAdd(T value)
    {
        int mask = _tableSize - 1;
        int slot = (int)((ulong.CreateTruncating(value) * _multiplier) >> _shift);
        while (true)
        {
            int stored = _table[slot];
            if (stored == 0)
            {
                break;
            }

            if (_entries[stored - 1] == value)
            {
                return stored - 1;
            }

            slot = (slot + 1) & mask;
        }

        if (_count == _entries.Length)
        {
            var larger = ArrayPool<T>.Shared.Rent(_entries.Length * 2);
            _entries.AsSpan(0, _count).CopyTo(larger);
            ArrayPool<T>.Shared.Return(_entries);
            _entries = larger;
        }

        _entries[_count] = value;
        _table[slot] = ++_count;
        if (_count * 2 > _tableSize)
        {
            Grow();
        }

        return _count - 1;
    }

    /// <summary>Doubles the table, so that it is never more than half full.</summary>
    private void Grow()
    {
        int size = _tableSize * 2;
        var table = NewTable(size);
        int mask = size - 1;
        int shift = 64 - BitOperations.Log2((uint)size);
        for (int entry = 0; entry < _count; entry++)
        {
            int slot = (int)((ulong.CreateTruncating(_entries[entry]) * _multiplier) >> shift);
            while (table[slot] != 0)
            {
                slot = (slot + 1) & mask;
            }

            table[slot] = entry + 1;
        }

        ArrayPool<int>.Shared.Return(_table);
        _table = table;
        _tableSize = size;
        _shift = shift;
    }

    /// <summary>A table of exactly <paramref name="size"/> empty slots (the pool may hand out an array that is bigger and dirty).</summary>
    private static int[] NewTable(int size)
    {
        var table = ArrayPool<int>.Shared.Rent(size);
        Array.Clear(table, 0, size);
        return table;
    }
}
