using ParquetNeo.Metadata;

namespace ParquetNeo.Serialization;

/// <summary>How a .NET type is stored in a column.</summary>
internal readonly record struct ColumnShape(PhysicalType Type, LogicalType? LogicalType, int? TypeLength = null);
