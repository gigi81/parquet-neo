namespace ParquetNeo.Tests.Storage;

/// <summary>A range of an object that a test storage was asked for.</summary>
public readonly record struct StorageRequest(string Key, long Offset, long Count);
