using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace ParquetNeo.Generator
{
    /// <summary>An array that is equal to another when its items are, so that a model that holds one can be cached between runs of the generator.</summary>
    internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IEnumerable<T>
        where T : IEquatable<T>
    {
        private readonly ImmutableArray<T> _items;

        public EquatableArray(ImmutableArray<T> items) => _items = items;

        public int Count => _items.IsDefault ? 0 : _items.Length;

        public T this[int index] => _items[index];

        public bool Equals(EquatableArray<T> other)
        {
            if (Count != other.Count)
            {
                return false;
            }

            for (int i = 0; i < Count; i++)
            {
                if (!_items[i].Equals(other._items[i]))
                {
                    return false;
                }
            }

            return true;
        }

        public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

        public override int GetHashCode()
        {
            int hash = Count;
            for (int i = 0; i < Count; i++)
            {
                hash = unchecked((hash * 31) + _items[i].GetHashCode());
            }

            return hash;
        }

        public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(_items.IsDefault ? ImmutableArray<T>.Empty : _items)).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
