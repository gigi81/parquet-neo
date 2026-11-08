namespace ParquetNeo.Serialization.Shredders;

/// <summary>Reads the value of an element of a collection; returns false when the element is null.</summary>
internal delegate bool ItemReader<in TItem, TValue>(TItem item, out TValue value);
