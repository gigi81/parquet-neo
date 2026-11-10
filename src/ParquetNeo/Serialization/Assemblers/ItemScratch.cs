using System.Runtime.CompilerServices;

namespace ParquetNeo.Serialization.Assemblers;

/// <summary>
/// Where the elements of a list are collected as they are read, so that the list or array that is returned is made once, with
/// exactly the room it needs, instead of growing as elements are added. One for each list of a row type, in each
/// <see cref="ReadContext"/> (the assemblers are shared by every read of the row type, so the scratch cannot be theirs).
/// </summary>
internal sealed class ItemScratch<TItem>
{
    private TItem[] _items = new TItem[16];

    public int Count { get; private set; }

    public void Add(TItem item)
    {
        if (Count == _items.Length)
        {
            Array.Resize(ref _items, _items.Length * 2);
        }

        _items[Count++] = item;
    }

    /// <summary>Starts over, whatever an earlier list that failed left.</summary>
    public void Clear() => Count = 0;

    /// <summary>The elements collected so far, which are valid until the next call of <see cref="Add"/> or <see cref="Clear"/>.</summary>
    public ReadOnlySpan<TItem> Items => _items.AsSpan(0, Count);

    /// <summary>Lets go of the elements of the list that was just made, so that the scratch does not keep them alive.</summary>
    public void Release()
    {
        if (RuntimeHelpers.IsReferenceOrContainsReferences<TItem>())
        {
            Array.Clear(_items, 0, Count);
        }

        Count = 0;
    }
}
