using ParquetNeo.Core;
using ParquetNeo.Metadata;

namespace ParquetNeo.Serialization.Buffers;

/// <summary>What the serializer needs to know about one column: where it is, which encoding and dictionary mode it asks for, and how to collect its values (the function takes the number of entries to make room for).</summary>
internal sealed record LeafInfo(string Path, ParquetEncoding? Encoding, DictionaryMode? Dictionary, Func<int, ILeafBuffer> CreateBuffer);
