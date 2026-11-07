using ParquetNeo.Schema;

namespace ParquetNeo.Nested;

/// <summary>Where the parts of a map are in the schema; see <see cref="NestedSchema.GetMapLayout"/>.</summary>
/// <param name="Container">The MAP group.</param>
/// <param name="KeyValue">The repeated group, one element of which is an entry.</param>
/// <param name="Key">The key: required.</param>
/// <param name="Value">The value, or null for a map that holds only keys (a set), which some old writers produced.</param>
public sealed record MapLayout(GroupNode Container, GroupNode KeyValue, SchemaNode Key, SchemaNode? Value);
