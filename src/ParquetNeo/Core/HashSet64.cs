namespace ParquetNeo.Core;

/// <summary>A set of 64-bit hashes in one open-addressing table of longs: what the bloom filter of a column chunk is sized from, and built out of, without sorting every value.</summary>
internal sealed class HashSet64
{
    private ulong[] _slots = new ulong[1024];
    private bool _hasZero;

    /// <summary>The number of different hashes.</summary>
    public int Count { get; private set; }

    public void Add(ulong hash)
    {
        // Zero marks an empty slot, so the hash zero is kept apart.
        if (hash == 0)
        {
            if (!_hasZero)
            {
                _hasZero = true;
                Count++;
            }

            return;
        }

        if (Count * 2 >= _slots.Length)
        {
            Grow();
        }

        if (Insert(_slots, hash))
        {
            Count++;
        }
    }

    public IEnumerable<ulong> Hashes()
    {
        if (_hasZero)
        {
            yield return 0;
        }

        foreach (ulong slot in _slots.Where(slot => slot != 0))
        {
            yield return slot;
        }
    }

    /// <summary>Puts the hash in the table; false when it was there. The hashes are well mixed already, so their low bits are the place to start.</summary>
    private static bool Insert(ulong[] slots, ulong hash)
    {
        int mask = slots.Length - 1;
        int index = (int)hash & mask;
        while (true)
        {
            ulong found = slots[index];
            if (found == 0)
            {
                slots[index] = hash;
                return true;
            }

            if (found == hash)
            {
                return false;
            }

            index = (index + 1) & mask;
        }
    }

    private void Grow()
    {
        var bigger = new ulong[_slots.Length * 2];
        foreach (ulong slot in _slots.Where(slot => slot != 0))
        {
            Insert(bigger, slot);
        }

        _slots = bigger;
    }
}
