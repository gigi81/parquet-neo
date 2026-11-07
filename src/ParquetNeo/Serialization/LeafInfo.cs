using ParquetNeo.Metadata;

namespace ParquetNeo.Serialization;

/// <summary>What the serializer needs to know about one column: where it is, which encoding it asks for, and how to collect its values.</summary>
internal sealed record LeafInfo(string Path, ParquetEncoding? Encoding, Func<ILeafBuffer> CreateBuffer);
