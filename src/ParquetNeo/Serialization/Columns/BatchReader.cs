using ParquetNeo.Core;

namespace ParquetNeo.Serialization.Columns;

/// <summary>Reads a batch of a column through the overload of <see cref="ColumnReader"/> that gives values of the .NET type.</summary>
internal delegate BatchRead BatchReader<TValue>(ColumnReader reader, Span<TValue> values, Span<int> definitionLevels, Span<int> repetitionLevels);
