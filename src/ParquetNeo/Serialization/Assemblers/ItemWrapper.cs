namespace ParquetNeo.Serialization.Assemblers;

/// <summary>Turns an element that was read into what the collection holds: the element itself, or a <see cref="Nullable{T}"/> of it.</summary>
internal delegate TItem ItemWrapper<in TElement, out TItem>(TElement element);
