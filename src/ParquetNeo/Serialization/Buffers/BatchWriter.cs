using ParquetNeo.Core;

namespace ParquetNeo.Serialization.Buffers;

/// <summary>Writes the values of a batch to a column through the overload of <see cref="ColumnWriter"/> that suits their type.</summary>
internal delegate void BatchWriter<TValue>(ColumnWriter writer, ReadOnlySpan<TValue> values, ReadOnlySpan<int> definitionLevels, ReadOnlySpan<int> repetitionLevels);
