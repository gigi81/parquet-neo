namespace ParquetNeo.Storage;

/// <summary>An object of a storage: its key and its size in bytes.</summary>
/// <param name="Key">The name of the object in the storage, with <c>/</c> between the parts of a path.</param>
/// <param name="Length">The size of the object.</param>
public readonly record struct StorageObject(string Key, long Length);
